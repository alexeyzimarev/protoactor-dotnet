﻿// -----------------------------------------------------------------------
// <copyright file="MongoIdentityStorage.cs" company="Asynkron AB">
//      Copyright (C) 2015-2020 Asynkron AB All rights reserved
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using static MongoDB.Driver.Builders<Proto.Cluster.Identity.MongoDb.PidLookupEntity>;

namespace Proto.Cluster.Identity.MongoDb
{
    public class MongoIdentityStorage : IIdentityStorage
    {
        private static readonly ILogger Logger = Log.CreateLogger<MongoIdentityStorage>();
        private static readonly Random Jitter = new();

        private readonly string _clusterName;
        private readonly IMongoCollection<PidLookupEntity> _pids;

        public MongoIdentityStorage(string clusterName, IMongoCollection<PidLookupEntity> pids)
        {
            ConnectionThrottlingPipeline.Initialize(pids.Database.Client);
            _clusterName = clusterName;
            _pids = pids;
        }

        public async Task<SpawnLock?> TryAcquireLock(
            ClusterIdentity clusterIdentity,
            CancellationToken ct
        )
        {
            var requestId = Guid.NewGuid().ToString();
            var hasLock = await TryAcquireLockAsync(clusterIdentity, requestId, ct).ConfigureAwait(false);
            return hasLock ? new SpawnLock(requestId, clusterIdentity) : null;
        }

        public async Task<StoredActivation?> WaitForActivation(
            ClusterIdentity clusterIdentity,
            CancellationToken ct
        )
        {
            var key = GetKey(clusterIdentity);
            var pidLookupEntity = await LookupKey(key, ct).ConfigureAwait(false);
            var lockId = pidLookupEntity?.LockedBy;

            if (lockId != null)
            {
                //There is an active lock on the pid, spin wait
                var i = 0;

                do
                {
                    await Task.Delay(Jitter.Next(20) + 100 * i, ct).ConfigureAwait(false);
                } while ((pidLookupEntity = await LookupKey(key, ct).ConfigureAwait(false))?.LockedBy == lockId && ++i < 10 &&
                         !ct.IsCancellationRequested);
            }

            //the lookup entity was lost, stale lock maybe?
            if (pidLookupEntity == null) return null;

            //lookup was unlocked, return this pid
            if (pidLookupEntity.LockedBy == null)
            {
                return new StoredActivation(
                    pidLookupEntity.MemberId!,
                    PID.FromAddress(pidLookupEntity.Address!, pidLookupEntity.UniqueIdentity!)
                );
            }

            //Still locked but not by the same request that originally locked it, so not stale
            if (pidLookupEntity.LockedBy != lockId) return null;

            //Stale lock. just delete it and let cluster retry
            // _logger.LogDebug($"Stale lock: {pidLookupEntity.Key}");
            await RemoveLock(new SpawnLock(lockId!, clusterIdentity), CancellationToken.None).ConfigureAwait(false);
            return null;
        }

        public Task RemoveLock(SpawnLock spawnLock, CancellationToken ct) =>
            _pids.DeleteManyAsync(p => p.LockedBy == spawnLock.LockId, ct);

        public async Task StoreActivation(string memberId, SpawnLock spawnLock, PID pid, CancellationToken ct)
        {
            Logger.LogDebug("Storing activation: {@ActivatorId}, {@SpawnLock}, {@PID}", memberId, spawnLock, pid);

            var key = GetKey(spawnLock.ClusterIdentity);

            var res = await ConnectionThrottlingPipeline.AddRequest(
                _pids.UpdateOneAsync(
                    s => s.Key == key && s.LockedBy == spawnLock.LockId && s.Revision == 1,
                    Update
                        .Set(l => l.Address, pid.Address)
                        .Set(l => l.MemberId, memberId)
                        .Set(l => l.UniqueIdentity, pid.Id)
                        .Set(l => l.Revision, 2)
                        .Unset(l => l.LockedBy),
                    new UpdateOptions(),
                    ct
                )
            ).ConfigureAwait(false);

            if (res.MatchedCount != 1)
                throw new LockNotFoundException($"Failed to store activation of {pid.ToShortString()}");
        }

        public Task RemoveActivation(PID pid, CancellationToken ct)
        {
            Logger.LogDebug("Removing activation: {@PID}", pid);

            return _pids.DeleteManyAsync(p => p.UniqueIdentity == pid.Id, ct);
        }

        public Task RemoveMember(string memberId, CancellationToken ct) =>
            _pids.DeleteManyAsync(p => p.MemberId == memberId, ct);

        public async Task<StoredActivation?> TryGetExistingActivation(
            ClusterIdentity clusterIdentity,
            CancellationToken ct
        )
        {
            var pidLookup = await LookupKey(GetKey(clusterIdentity), ct).ConfigureAwait(false);

            return pidLookup == null || pidLookup.Address == null || pidLookup.UniqueIdentity == null
                ? null
                : new StoredActivation(
                    pidLookup.MemberId!,
                    PID.FromAddress(pidLookup.Address, pidLookup.UniqueIdentity)
                );
        }

        public void Dispose() => GC.SuppressFinalize(this);

        public Task Init() => _pids.Indexes.CreateOneAsync(new CreateIndexModel<PidLookupEntity>("{ MemberId: 1 }"));

        private async Task<bool> TryAcquireLockAsync(
            ClusterIdentity clusterIdentity,
            string requestId,
            CancellationToken ct
        )
        {
            var key = GetKey(clusterIdentity);

            var lockEntity = new PidLookupEntity
            {
                Address = null,
                Identity = clusterIdentity.Identity,
                Key = key,
                Kind = clusterIdentity.Kind,
                LockedBy = requestId,
                Revision = 1,
                MemberId = null
            };

            try
            {
                //be 100% sure own the lock here
                await ConnectionThrottlingPipeline.AddRequest(
                    _pids.InsertOneAsync(lockEntity, new InsertOneOptions(), ct )
                ).ConfigureAwait(false);
                
                Logger.LogDebug("Got lock on first try for {ClusterIdentity}", clusterIdentity);
                
                return true;
            }
            catch (MongoWriteException)
            {
                var l = await ConnectionThrottlingPipeline.AddRequest(
                    _pids.ReplaceOneAsync(
                        x => x.Key == key && x.LockedBy == null && x.Revision == 0,
                        lockEntity,
                        new ReplaceOptions
                        {
                            IsUpsert = false
                        },
                        ct
                    )
                ).ConfigureAwait(false);

                //if l.MatchCount == 1, then one document was updated by us, and we should own the lock, no?
                var gotLock = l.IsAcknowledged && l.ModifiedCount == 1;
                Logger.LogDebug("Did {Got} get lock on second try for {ClusterIdentity}", gotLock ? "" : "not ",
                    clusterIdentity
                );
                
                return gotLock;
            }
        }

        private async Task<PidLookupEntity?> LookupKey(string key, CancellationToken ct)
            => await ConnectionThrottlingPipeline.AddRequest(_pids.Find(x => x.Key == key).Limit(1)
                .SingleOrDefaultAsync(ct)
            ).ConfigureAwait(false);

        private string GetKey(ClusterIdentity clusterIdentity) => $"{_clusterName}/{clusterIdentity.ToShortString()}";
    }
}