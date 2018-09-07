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

namespace DurableTask.ServiceBus.Tests
{
    using DurableTask.Core;
    using DurableTask.ServiceBus.Tracking;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using Microsoft.WindowsAzure.Storage.Table;

    [TestClass]
    public class AzureTableClientTest
    {
        private const string ConnectionString = "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;" +
            "AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;" +
            "BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;" +
            "TableEndpoint=http://127.0.0.1:10002/devstoreaccount1;" +
            "QueueEndpoint=http://127.0.0.1:10001/devstoreaccount1;";

        [TestMethod]
        public void CreateQueryWithoutFilter()
        {
            var tableClient = new AzureTableClient("myHub", ConnectionString);
            var stateQuery = new OrchestrationStateQuery();

            TableQuery<AzureTableOrchestrationStateEntity> query = tableClient.CreateQueryInternal(stateQuery, 1, false);

            Assert.AreEqual("(PartitionKey eq 'IS')", query.FilterString);
        }

        [TestMethod]
        public void CreateQueryWithPrimaryFilter()
        {
            var tableClient = new AzureTableClient("myHub", ConnectionString);
            var stateQuery = new OrchestrationStateQuery();
            stateQuery.AddInstanceFilter("myInstance");

            TableQuery<AzureTableOrchestrationStateEntity> query = tableClient.CreateQueryInternal(stateQuery, 1, false);

            Assert.AreEqual("(PartitionKey eq 'IS') and (RowKey ge 'ID_EID_myInstance') and (RowKey lt 'ID_EID_myInstancf')", query.FilterString);
        }

        [TestMethod]
        public void CreateQueryWithPrimaryAndSecondaryFilter()
        {
            var tableClient = new AzureTableClient("myHub", ConnectionString);
            var stateQuery = new OrchestrationStateQuery();
            stateQuery.AddInstanceFilter("myInstance");
            stateQuery.AddNameVersionFilter("myName");

            TableQuery<AzureTableOrchestrationStateEntity> query = tableClient.CreateQueryInternal(stateQuery, 1, false);

            Assert.AreEqual("(PartitionKey eq 'IS') and (RowKey ge 'ID_EID_myInstance') and (RowKey lt 'ID_EID_myInstancf') and (InstanceId eq 'myInstance') and (Name eq 'myName')", query.FilterString);
        }
    }
}
