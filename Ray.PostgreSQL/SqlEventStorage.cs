﻿using Dapper;
using Npgsql;
using NpgsqlTypes;
using ProtoBuf;
using Ray.Core.EventSourcing;
using Ray.Core.Message;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Ray.PostgreSQL
{
    public class SqlEventStorage<K> : IEventStorage<K>
    {
        SqlGrainConfig tableInfo;
        Channel<EventBytesTransactionWrap<K>> EventSaveFlowChannel = Channel.CreateUnbounded<EventBytesTransactionWrap<K>>();
        int isProcessing = 0;
        public SqlEventStorage(SqlGrainConfig tableInfo)
        {
            this.tableInfo = tableInfo;
        }
        public async Task<IList<IEventBase<K>>> GetListAsync(K stateId, Int64 startVersion, Int64 endVersion, DateTime? startTime = null)
        {
            var originList = new List<SqlEvent>((int)(endVersion - startVersion));
            await Task.Run(async () =>
            {
                var tableList = await tableInfo.GetTableList(startTime);
                using (var conn = tableInfo.CreateConnection() as NpgsqlConnection)
                {
                    await conn.OpenAsync();
                    foreach (var table in tableList)
                    {
                        var sql = $"COPY (SELECT typecode,data from {table.Name} WHERE stateid='{stateId.ToString()}' and version>{startVersion} and version<={endVersion} order by version asc) TO STDOUT (FORMAT BINARY)";
                        using (var reader = conn.BeginBinaryExport(sql))
                        {
                            while (reader.StartRow() != -1)
                            {
                                originList.Add(new SqlEvent { TypeCode = reader.Read<string>(NpgsqlDbType.Varchar), Data = reader.Read<byte[]>(NpgsqlDbType.Bytea) });
                            }
                        }
                    }
                }
            }).ConfigureAwait(false);

            var list = new List<IEventBase<K>>(originList.Count);
            foreach (var origin in originList)
            {
                if (MessageTypeMapper.EventTypeDict.TryGetValue(origin.TypeCode, out var type))
                {
                    using (var ms = new MemoryStream(origin.Data))
                    {
                        if (Serializer.Deserialize(type, ms) is IEventBase<K> evt)
                        {
                            list.Add(evt);
                        }
                    }
                }
            }
            return list;
        }
        public async Task<IList<IEventBase<K>>> GetListAsync(K stateId, string typeCode, Int64 startVersion, Int32 limit, DateTime? startTime = null)
        {
            var originList = new List<byte[]>(limit);
            if (MessageTypeMapper.EventTypeDict.TryGetValue(typeCode, out var type))
            {
                await Task.Run(async () =>
                {
                    var tableList = await tableInfo.GetTableList(startTime);
                    using (var conn = tableInfo.CreateConnection() as NpgsqlConnection)
                    {
                        await conn.OpenAsync();
                        foreach (var table in tableList)
                        {
                            var sql = $"COPY (SELECT data from {table.Name} WHERE stateid='{stateId.ToString()}' and typecode='{typeCode}' and version>{startVersion} order by version asc limit {limit}) TO STDOUT (FORMAT BINARY)";
                            using (var reader = conn.BeginBinaryExport(sql))
                            {
                                while (reader.StartRow() != -1)
                                {
                                    originList.Add(reader.Read<byte[]>(NpgsqlDbType.Bytea));
                                }
                            }
                            if (originList.Count >= limit)
                                break;
                        }
                    }
                }).ConfigureAwait(false);
            }
            var list = new List<IEventBase<K>>(originList.Count);
            foreach (var origin in originList)
            {
                using (var ms = new MemoryStream(origin))
                {
                    if (Serializer.Deserialize(type, ms) is IEventBase<K> evt)
                    {
                        list.Add(evt);
                    }
                }
            }
            return list;
        }

        static ConcurrentDictionary<string, string> saveSqlDict = new ConcurrentDictionary<string, string>();
        public async ValueTask<bool> SaveAsync(IEventBase<K> evt, byte[] bytes, string uniqueId = null)
        {
            var wrap = EventBytesTransactionWrap<K>.Create(evt, bytes, uniqueId);
            await EventSaveFlowChannel.Writer.WriteAsync(wrap);
            if (isProcessing == 0)
                TriggerFlowProcess().GetAwaiter();
            return await wrap.TaskSource.Task;
        }
        private async ValueTask TriggerFlowProcess()
        {
            if (Interlocked.CompareExchange(ref isProcessing, 1, 0) == 0)
            {
                while (await EventSaveFlowChannel.Reader.WaitToReadAsync())
                {
                    while (await FlowProcess()) { }
                }
                Interlocked.Exchange(ref isProcessing, 0);
            }
        }
        private async ValueTask<bool> FlowProcess()
        {
            return await Task.Run(async () =>
             {
                 if (EventSaveFlowChannel.Reader.TryRead(out var first))
                 {
                     var start = DateTime.UtcNow;
                     var wrapList = new List<EventBytesTransactionWrap<K>>
                     {
                         first
                     };
                     var table = await tableInfo.GetTable(DateTime.UtcNow);
                     if (!copySaveSqlDict.TryGetValue(table.Name, out var saveSql))
                     {
                         saveSql = $"copy {table.Name}(stateid,uniqueId,typecode,data,version) FROM STDIN (FORMAT BINARY)";
                         copySaveSqlDict.TryAdd(table.Name, saveSql);
                     }
                     try
                     {
                         using (var conn = tableInfo.CreateConnection() as NpgsqlConnection)
                         {
                             await conn.OpenAsync();
                             using (var writer = conn.BeginBinaryImport(saveSql))
                             {
                                 writer.StartRow();
                                 writer.Write(first.Value.StateId.ToString(), NpgsqlDbType.Varchar);
                                 writer.Write(first.UniqueId, NpgsqlDbType.Varchar);
                                 writer.Write(first.Value.TypeCode, NpgsqlDbType.Varchar);
                                 writer.Write(first.Bytes, NpgsqlDbType.Bytea);
                                 writer.Write(first.Value.Version, NpgsqlDbType.Bigint);
                                 while (EventSaveFlowChannel.Reader.TryRead(out var evt))
                                 {
                                     wrapList.Add(evt);
                                     writer.StartRow();
                                     writer.Write(evt.Value.StateId.ToString(), NpgsqlDbType.Varchar);
                                     writer.Write(evt.UniqueId, NpgsqlDbType.Varchar);
                                     writer.Write(evt.Value.TypeCode, NpgsqlDbType.Varchar);
                                     writer.Write(evt.Bytes, NpgsqlDbType.Bytea);
                                     writer.Write(evt.Value.Version, NpgsqlDbType.Bigint);
                                     if ((DateTime.UtcNow - start).TotalMilliseconds > 50) break;//保证批量延时不超过50ms
                                 }
                                 writer.Complete();
                             }
                         }
                         foreach (var wrap in wrapList)
                         {
                             wrap.TaskSource.SetResult(true);
                         }
                     }
                     catch
                     {
                         foreach (var data in wrapList)
                         {
                             try
                             {
                                 data.TaskSource.TrySetResult(await SingleSaveAsync(data.Value, data.Bytes, data.UniqueId));
                             }
                             catch (Exception e)
                             {
                                 data.TaskSource.TrySetException(e);
                             }
                         }
                     }
                     //返回true代表有接收到数据
                     return true;
                 }
                 //没有接收到数据
                 return false;
             }).ConfigureAwait(false);
        }
        private async ValueTask<bool> SingleSaveAsync(IEventBase<K> evt, byte[] bytes, string uniqueId = null)
        {
            var table = await tableInfo.GetTable(evt.Timestamp);
            if (!saveSqlDict.TryGetValue(table.Name, out var saveSql))
            {
                saveSql = $"INSERT INTO {table.Name}(stateid,uniqueId,typecode,data,version) VALUES(@StateId,@UniqueId,@TypeCode,@Data,@Version)";
                saveSqlDict.TryAdd(table.Name, saveSql);
            }
            try
            {
                using (var conn = tableInfo.CreateConnection())
                {
                    return (await conn.ExecuteAsync(saveSql, new { StateId = evt.StateId.ToString(), UniqueId = uniqueId, evt.TypeCode, Data = bytes, evt.Version })) > 0;
                }
            }
            catch (Exception ex)
            {
                if (!(ex is PostgresException e && e.SqlState == "23505"))
                {
                    throw ex;
                }
            }
            return false;
        }
        static ConcurrentDictionary<string, string> copySaveSqlDict = new ConcurrentDictionary<string, string>();
        public async ValueTask<bool> BatchSaveAsync(List<EventSaveWrap<K>> list)
        {
            var table = await tableInfo.GetTable(DateTime.UtcNow);
            if (list.Count > 1)
            {
                if (!copySaveSqlDict.TryGetValue(table.Name, out var saveSql))
                {
                    saveSql = $"copy {table.Name}(stateid,uniqueId,typecode,data,version) FROM STDIN (FORMAT BINARY)";
                    copySaveSqlDict.TryAdd(table.Name, saveSql);
                }
                await Task.Run(async () =>
                {
                    using (var conn = tableInfo.CreateConnection() as NpgsqlConnection)
                    {
                        await conn.OpenAsync();
                        using (var writer = conn.BeginBinaryImport(saveSql))
                        {
                            foreach (var evt in list)
                            {
                                writer.StartRow();
                                writer.Write(evt.Evt.StateId.ToString(), NpgsqlDbType.Varchar);
                                writer.Write(evt.UniqueId, NpgsqlDbType.Varchar);
                                writer.Write(evt.Evt.TypeCode, NpgsqlDbType.Varchar);
                                writer.Write(evt.Bytes, NpgsqlDbType.Bytea);
                                writer.Write(evt.Evt.Version, NpgsqlDbType.Bigint);
                            }
                            writer.Complete();
                        }
                    }
                }).ConfigureAwait(false);
                return true;
            }
            else
            {
                var evt = list[0];
                return await SingleSaveAsync(evt.Evt, evt.Bytes, evt.UniqueId);
            }
        }
    }
}
