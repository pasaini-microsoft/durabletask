﻿//  ----------------------------------------------------------------------------------
//  Copyright Microsoft Corporation
//  Licensed under the Apache License, Version 2.0 (the "License");
//  you may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
//  http://www.apache.org/licenses/LICENSE-2.0
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//  limitations under the License.
//  ----------------------------------------------------------------------------------

#nullable enable
namespace DurableTask.AzureStorage.Partitioning
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Azure;
    using Azure.Data.Tables;
    using DurableTask.AzureStorage.Storage;

    /// <summary>
    /// Partition ManagerV3 is based on the Table storage.  
    /// </summary>
    sealed class TablePartitionManager : IPartitionManager, IDisposable
    {
        readonly AzureStorageClient azureStorageClient;
        readonly AzureStorageOrchestrationService service;
        readonly AzureStorageOrchestrationServiceSettings settings;
        readonly CancellationTokenSource partitionManagerCancellationSource;
        readonly string connectionString;
        readonly string storageAccountName;
        readonly TableServiceClient tableServiceClient;
        readonly TableClient partitionTable;
        readonly TableLeaseManager tableLeaseManager;

        /// <summary>
        /// constructor to initiate new instances of TablePartitionManager
        /// </summary>
        /// <param name="azureStorageClient">Client for the storage account.</param>
        /// <param name="service">The service responsible for initiating or terminating the partition manager.</param>
        public TablePartitionManager(
            AzureStorageOrchestrationService service,
            AzureStorageClient azureStorageClient)
        {
            this.azureStorageClient = azureStorageClient;
            this.service = service;
            this.settings = this.azureStorageClient.Settings;
            this.connectionString = this.settings.StorageConnectionString ?? this.settings.StorageAccountDetails.ConnectionString;
            this.storageAccountName = this.azureStorageClient.TableAccountName;
            if(this.connectionString == null)
            {
                throw new Exception("Connection string is null. Managed identity is not supported in the table partition manager yet.");
            }
            this.partitionManagerCancellationSource = new CancellationTokenSource();
            this.tableServiceClient = new TableServiceClient(this.connectionString);
            this.partitionTable = this.tableServiceClient.GetTableClient(this.settings.PartitionTableName);
            this.tableLeaseManager = new TableLeaseManager(this.partitionTable, this.service, this.settings, this.storageAccountName);
        }

        
        /// <summary>
        /// This method create a new instance of the class TableLeaseManager that represents the worker. 
        /// And then start the loop that the worker keeps operating on the table. 
        /// </summary>
        async Task IPartitionManager.StartAsync()
        {
            await Task.Factory.StartNew(() => this.PartitionManagerLoop(this.partitionManagerCancellationSource.Token));
            this.settings.Logger.PartitionManagerInfo(
                this.storageAccountName,
                this.settings.TaskHubName,
                this.settings.WorkerId,
                "", //Empty string as it does not target any particular partition, but rather only initiates the partition manager.
                $"Worker {this.settings.WorkerId} starts acquiring and balancing leases.");
        }

        async Task PartitionManagerLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                TimeSpan timeToSleep = this.settings.LeaseRenewInterval;
                try
                {
                    ReadTableReponse response = await this.tableLeaseManager.ReadAndWriteTable();
                    if (response.WorkOnRelease || response.WaitForPartition)
                    {
                        timeToSleep = TimeSpan.FromSeconds(1);
                    }
                }
                catch
                {
                    // if the worker failed to update the table, re-read the table immediately without waiting.
                    timeToSleep = TimeSpan.FromSeconds(0);
                }
                await Task.Delay(timeToSleep, token);
            }
        }

        /// <summary>
        /// This method will stop the partition manager. It first stops the task ReadAndWriteTable().
        /// And then start the Task ShutDown() until all the leases in the worker is drained. 
        /// </summary>
        async Task IPartitionManager.StopAsync()
        {
            this.partitionManagerCancellationSource.Cancel();
            this.settings.Logger.PartitionManagerInfo(
                this.storageAccountName,
                this.settings.TaskHubName,
                this.settings.WorkerId,
                "",
                $"Worker {this.settings.WorkerId} starts draining all ownership leases.");

            bool isFinish = false;
            //Shutting down is to drain all the current leases and then release them.
            //Thus the worker checks table every 1 second to see if the realease of all ownership lease finishes to ensure timely updates.
            TimeSpan timeToSleep = TimeSpan.FromSeconds(1);

            while (!isFinish)
            {
                try
                {
                    isFinish = await this.tableLeaseManager.ShutDown();
                }
                catch
                {
                    //if the worker fails to update the table, re-read the table immediately without wait.
                    timeToSleep = TimeSpan.FromSeconds(0);
                }
                await Task.Delay(timeToSleep);
            };

            this.settings.Logger.PartitionManagerInfo(
                this.storageAccountName,
                this.settings.TaskHubName,
                this.settings.WorkerId,
                "",
                $"Worker {this.settings.WorkerId} releases all ownership leases.");
        }

        async Task IPartitionManager.CreateLeaseStore()
        {
            await this.partitionTable.CreateIfNotExistsAsync();
        }

        async Task IPartitionManager.CreateLease(string leaseName)
        {
            try
            {
                var lease = new TableLease()
                {
                    PartitionKey = "",
                    RowKey = leaseName
                };
                await this.partitionTable.AddEntityAsync(lease);
            }
            catch (RequestFailedException e) when (e.Status == 409 /* The specified entity already exists. */)
            {
                this.settings.Logger.PartitionManagerInfo(
                    this.storageAccountName,
                    this.settings.TaskHubName,
                    this.settings.WorkerId,
                    leaseName,
                    $"The partition {leaseName} already exists in the table.");
            }
        }
        
        Task<IEnumerable<BlobLease>> IPartitionManager.GetOwnershipBlobLeases()
        {
            throw new NotImplementedException("This method is not implemented in the TablePartitionManager");
        }

        /// <summary>
        /// internal use for testing . 
        /// </summary>
        internal IEnumerable<TableLease> GetTableLeases()
        {
            return this.partitionTable.Query<TableLease>();
        }

        Task IPartitionManager.DeleteLeases()
        {
            return this.partitionTable.DeleteAsync();
        }

        sealed class TableLeaseManager
        {
            readonly string workerName;
            readonly AzureStorageOrchestrationService service;
            readonly AzureStorageOrchestrationServiceSettings settings;
            readonly TableClient partitionTable;
            readonly string storageAccountName;
            readonly Dictionary<string, Task> tasks;

            public TableLeaseManager(TableClient table, AzureStorageOrchestrationService service, AzureStorageOrchestrationServiceSettings settings, string storageAccountName)
            {
                this.partitionTable = table;
                this.service = service;
                this.settings = settings;
                this.storageAccountName = storageAccountName;
                this.workerName = this.settings.WorkerId;
                this.tasks = new Dictionary<string, Task>();
            }

            /// <summary>
            /// This method called in the PartitionManagerLoop. It reads the partition table and then determines the tasks the worker should do.
            /// </summary>
            /// <returns>
            /// Returns <c>null</c> if the worker successfully updates the table.
            /// </returns>
            /// <exception cref="RequestFailedException">Thrown if failed to update the table.
            /// </exception>
            public async Task<ReadTableReponse> ReadAndWriteTable()
            {
                var response = new ReadTableReponse();
                Pageable<TableLease> partitions = partitionTable.Query<TableLease>();
                var partitionDistribution = new Dictionary<string, List<TableLease>>(); 
                int leaseNum = 0;

                foreach (TableLease partition in partitions)
                {
                    // Check to see if we're listening to any queues that we shouldn't be.
                    // If so, remove them from the OwnedControlQueues to stop listening to it.
                    this.service.DropLostControlQueues(partition);

                    bool isClaimedLease = false;
                    bool isStealedLease = false;
                    bool isRenewdLease = false;
                    bool isDrainedLease = false;
                    bool isReleasedLease = false;
                    ETag etag = partition.ETag;

                    IsLeaseAvailableToClaim(partition);
                    CheckOtherWorkerLease(partition, partitionDistribution, response);
                    CheckOwnershipLease(partition, leaseNum, response);

                    // Update the table if the lease is claimed, stolen, renewed, drained or released.
                    if (isClaimedLease || isStealedLease || isRenewdLease || isDrainedLease || isReleasedLease)
                    {
                        try
                        {
                            await this.partitionTable.UpdateEntityAsync(partition, etag, (TableUpdateMode)1);
                            if(isClaimedLease)
                            {
                                await this.service.TableLeaseAcquiredAsync(partition);
                                this.settings.Logger.PartitionManagerInfo(
                                    this.storageAccountName,
                                    this.settings.TaskHubName,
                                    this.settings.WorkerId,
                                    partition.RowKey,
                                    $"Worker {this.settings.WorkerId} acquires the lease of {partition.RowKey}.");
                            }
                            if (isStealedLease)
                            {
                                this.settings.Logger.PartitionManagerInfo(
                                this.storageAccountName,
                                this.settings.TaskHubName,
                                this.settings.WorkerId,
                                partition.RowKey,
                                $"Worker {this.settings.WorkerId} steals the lease of {partition.RowKey}.");
                            }
                            if (isReleasedLease)
                            {
                                this.settings.Logger.PartitionManagerInfo(
                                this.storageAccountName,
                                this.settings.TaskHubName,
                                this.settings.WorkerId,
                                partition.RowKey,
                                $"Worker {this.settings.WorkerId} releases the lease of {partition.RowKey}.");
                            }
                            if(isDrainedLease)
                            {
                                this.settings.Logger.PartitionManagerInfo(
                                this.storageAccountName,
                                this.settings.TaskHubName,
                                this.settings.WorkerId,
                                partition.RowKey,
                                $"Worker {this.settings.WorkerId} starts draining {partition.RowKey}.");
                            }
                        }
                        //Exception will be thrown if the update operation fails.
                        //Need to re-read the table again to get the latest ETag.
                        catch (RequestFailedException ex) when (ex.Status == 412)
                        {
                            throw ex;
                        }
                    }

                    //Check if a lease is available for the current worker.
                    void IsLeaseAvailableToClaim(TableLease partition)
                    {
                        //Check if the partition is empty, expired or stolen by the current worker and claim it.
                        bool isEmptyLease = (partition.CurrentOwner == null && partition.NextOwner == null);
                        bool isExpired = (DateTime.UtcNow >= partition.ExpiresAt && partition.NextOwner == null);
                        bool isStolenByMe = (partition.CurrentOwner == null && partition.NextOwner == this.workerName);
                        // This condition is for the case that workers becomes unhealthy and then force to steal.
                        bool isExpiredForTooLong = (DateTime.UtcNow - partition.ExpiresAt > TimeSpan.FromMinutes(3));

                        if (isEmptyLease || isExpired || isStolenByMe || isExpiredForTooLong)
                        {
                            this.ClaimLease(partition);
                            isClaimedLease = true;
                        }
                    }
                    
                    //Check ownership lease. 
                    // If the lease is not stolen by others, renew it.
                    // If the lease is stolen by others, check if starts drainning or if finishes drainning.
                    void CheckOwnershipLease(TableLease partition, int leaseNum, ReadTableReponse response)
                    {
                        if (partition.CurrentOwner == this.workerName)
                        {
                            if (partition.NextOwner == null)
                            {
                                leaseNum++;
                            }
                            else
                            {
                                response.WorkOnRelease = true;

                                if (partition.IsDraining)
                                {
                                    if (this.tasks[partition.RowKey!].IsCompleted == true)
                                    {
                                        this.ReleaseLease(partition);
                                        isReleasedLease = true;
                                    }
                                }
                                else
                                {
                                    this.DrainLease(partition, CloseReason.LeaseLost);
                                    isDrainedLease = true;
                                }
                            }
                            if (partition.CurrentOwner != null)
                            {
                                this.RenewLease(partition);
                                isRenewdLease = true;
                            }
                        }
                    }

                    //If the lease is other worker's lease. Store it to the dictionary for future balance.
                    //If the other worker is shutting down, steal the lease.
                    void CheckOtherWorkerLease(TableLease partition, Dictionary<string, List<TableLease>> partitionDistribution, ReadTableReponse response)
                    {
                        bool isOtherWorkerCurrentLease = (partition.CurrentOwner != this.workerName && partition.NextOwner == null && partition.IsDraining == false);
                        bool isAnyWorkerFutureLease = (partition.CurrentOwner != this.workerName && partition.NextOwner != null);
                        bool isOtherWorkerShutDownLease = (partition.CurrentOwner != this.workerName && partition.NextOwner == null && partition.IsDraining == true);

                        //If the lease is other worker's current lease, add partition to the dictionary with CurrentOwner as key.
                        if (isOtherWorkerCurrentLease)
                        {
                            string currentOwner = partition.CurrentOwner!;
                            if (partitionDistribution.ContainsKey(currentOwner))
                            {
                                partitionDistribution[currentOwner].Add(partition);
                            }
                            else
                            {
                                partitionDistribution.Add(currentOwner, new List<TableLease> { partition });
                            }
                        }

                        // If other workers' lease is stolen, suppose lease tranfer could finish successfully, and add partition to the dictionary with NextOwner as key. 
                        if (isAnyWorkerFutureLease)
                        {
                            string nextOwner = partition.NextOwner!;
                            //If the NextOwner of the lease is the current worker, just plus 1 to the leaseNum.
                            if (nextOwner == this.workerName)
                            {
                                leaseNum++;
                                response.WaitForPartition = true;
                            }
                            //If the lease is stolen by other workers, add it to the partitionDistribution dictionary with NextOwner as key.
                            else
                            {
                                if (partitionDistribution.ContainsKey(nextOwner))
                                {
                                    partitionDistribution[nextOwner].Add(partition);
                                }
                                else
                                {
                                    partitionDistribution.Add(nextOwner, new List<TableLease> { partition });
                                }
                            }
                        }

                        //If the lease belongs to a worker that is shutting down, steal it.
                        if (isOtherWorkerShutDownLease)
                        {
                            this.StealLease(partition);
                            isStealedLease = true;
                            response.WaitForPartition = true;
                        }
                    }

                }

                // Balancing leases.
                try
                {
                    await this.LeaseBalancer(partitionDistribution, partitions, leaseNum, response);
                }
                catch (RequestFailedException ex) when (ex.Status == 412)
                {
                    throw ex;
                }
                

                return response;

            }

            //This is for the balance process.
            //First check if there is any other worker, if not, skip the balance process.
            //If there is, then calculate the number of leases per worker for balance.
            //If owned lease are less than the balancing number, then steal lease from other workers whose lease is more than balancing number.
            //Exception will be thrown if the update operation fails.
            public async Task LeaseBalancer(Dictionary<string, List<TableLease>> partitionDistribution, Pageable<TableLease> partitions, int leaseNum, ReadTableReponse response)
            {
                if (partitionDistribution.Count != 0)
                {
                    int numLeasePerWorkerForBalance = (partitions.Count()) / (partitionDistribution.Count + 1);
                    //If the number of leases per worker is 0, then set it to 1.
                    if (numLeasePerWorkerForBalance == 0)
                    {
                        numLeasePerWorkerForBalance = 1;
                    }
                    int numOfLeaseToSteal = numLeasePerWorkerForBalance - leaseNum;

                    while (numOfLeaseToSteal > 0)
                    {
                        int checkedPartitionCount = 0;
                        foreach (KeyValuePair<string, List<TableLease>> pair in partitionDistribution)
                        {
                            checkedPartitionCount++;
                            int currentWorkerNumofLeases = pair.Value.Count;
                            if (currentWorkerNumofLeases > numLeasePerWorkerForBalance)
                            {
                                foreach (TableLease partition in pair.Value)
                                {
                                    {
                                        numOfLeaseToSteal--;
                                        currentWorkerNumofLeases--;
                                        ETag etag = partition.ETag;
                                        this.StealLease(partition);
                                        try
                                        {
                                            await this.partitionTable.UpdateEntityAsync(partition, etag, (TableUpdateMode)1);
                                            this.settings.Logger.PartitionManagerInfo(
                                                this.storageAccountName,
                                                this.settings.TaskHubName,
                                                this.settings.WorkerId,
                                                partition.RowKey,
                                                $"Worker {this.settings.WorkerId} steals the lease of {partition.RowKey}.");
                                            response.WaitForPartition = true;
                                        }
                                        catch (RequestFailedException ex) when (ex.Status == 412)
                                        {
                                            throw ex;
                                        }
                                    }

                                    if (currentWorkerNumofLeases == numLeasePerWorkerForBalance || numOfLeaseToSteal == 0) { break; }
                                }
                            }
                            if (numOfLeaseToSteal == 0) { break; }
                        }
                        if (checkedPartitionCount == partitionDistribution.Count) { break; }
                    }
                }
            }

            public void ClaimLease(TableLease lease)
            {
                lease.CurrentOwner = this.workerName;
                lease.NextOwner = null;
                lease.OwnedSince = DateTime.UtcNow;
                lease.LastRenewal = DateTime.UtcNow;
                lease.ExpiresAt = DateTime.UtcNow.AddMinutes(1);
                lease.IsDraining = false;
            }

            public void DrainLease(TableLease lease, CloseReason reason)
            {
                lease.IsDraining = true;
                var task = Task.Run(() => this.service.TableLeaseDrainAsync(lease, reason));
                string partitionId = lease.RowKey!;
                if (this.tasks.ContainsKey(partitionId))
                {
                    this.tasks[partitionId] = task;
                }
                else
                {
                    this.tasks.Add(partitionId, task);
                }
            }

            public void ReleaseLease(TableLease lease)
            {
                lease.IsDraining = false;
                lease.CurrentOwner = null;
            }

            public void RenewLease(TableLease lease) 
            {
                lease.ExpiresAt = DateTime.UtcNow.AddSeconds(30);
            }

            public void StealLease(TableLease lease)
            {
                lease.NextOwner = this.workerName;
            }

            /// <summary>
            /// Used to stop the partition manager. It first completes all ownership leases and then stops the partition manager.
            /// </summary>
            /// <returns>
            /// Returns <c>null</c> if the worker successfully updates the table.
            /// </returns>
            /// <exception cref="RequestFailedException">Thrown if failed to update the table.
            /// </exception>
            public async Task<bool> ShutDown()
            {
                Pageable<TableLease> partitions = partitionTable.Query<TableLease>();
                int leaseNum = 0;
                foreach (TableLease partition in partitions)
                {
                    bool isDrainedLease = false;
                    bool isReleasedLease = false;

                    if (partition.CurrentOwner == workerName)
                    {
                        leaseNum++;
                        ETag etag = partition.ETag;
                        
                        if (partition.IsDraining)
                        {
                            if (tasks[partition.RowKey!].IsCompleted == true)
                            {
                                ReleaseLease(partition);
                                isReleasedLease = true;
                                leaseNum--;
                            }
                            else
                            {
                                RenewLease(partition);
                            }
                        }
                        else
                        {
                            DrainLease(partition, CloseReason.Shutdown);
                            RenewLease(partition);
                            isDrainedLease = true;
                        }
                        
                        try
                        {
                            await this.partitionTable.UpdateEntityAsync(partition, etag, (TableUpdateMode)1);
                            if (isDrainedLease)
                            {
                                this.settings.Logger.PartitionManagerInfo(
                                     this.storageAccountName,
                                     this.settings.TaskHubName,
                                     this.settings.WorkerId,
                                     partition.RowKey,
                                     $"Worker {this.settings.WorkerId} starts draining the lease of{partition.RowKey}.");
                            }
                            if (isReleasedLease)
                            {
                                this.settings.Logger.PartitionManagerInfo(
                                     this.storageAccountName,
                                     this.settings.TaskHubName,
                                     this.settings.WorkerId,
                                     partition.RowKey,
                                     $"Worker {this.settings.WorkerId} releases the lease of {partition.RowKey}.");
                            }
                            
                        }
                        //Exception will be thrown if update operation fails.
                        //Need to re-read the table again to get the latest ETag.
                        catch (RequestFailedException ex) when (ex.Status == 412)
                        {
                            throw ex;
                        }
                    }

                }
                var isReleasedAllLease = (leaseNum == 0);
                return isReleasedAllLease;
            }
        }

        /// <summary>
        ///The Response class describes the behavior of the ReadandWrite method in the PartitionManager worker class. 
        ///If the virtual machine is about to be drained, the method sets the WorkonRelease flag to true. 
        ///If the VM is going to acquire another lease, it sets the waitforPartition flag to true. 
        ///When either of these flags is true, the sleep time of the VM changes from 15 seconds to 1 second.
        /// </summary>
        class ReadTableReponse
        {
            //If set to true, it indicates that the VM is working on release lease. 
            public bool WorkOnRelease { get; set; } = false;
            
            //If set to true, it indicates that the VM is waiting for a lease to be released.
            public bool WaitForPartition { get; set; } = false;
        }

        //only used for testing.
        internal void SimulateUnhealthyWorker(CancellationToken testToken)
        {
            _ = Task.Run(() => this.PartitionManagerLoop(testToken));
        }

        //internal used for testing
        internal void KillLoop()
        {
            this.partitionManagerCancellationSource.Cancel();
        }

        public void Dispose()
        {
            partitionManagerCancellationSource.Dispose();
        }
    }
}