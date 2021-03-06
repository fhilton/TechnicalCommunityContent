﻿namespace Microsoft.Azure.Devices.Applications.PredictiveMaintenance.Common.Helpers
{
    using System;
    using System.Net;
    using System.Threading.Tasks;
    using WindowsAzure.Storage;
    using WindowsAzure.Storage.Table;
    using Models;

    public static class AzureTableStorageHelper
    {
        public static async Task<CloudTable> GetTableAsync(string storageConnectionString, string tableName)
        {
            CloudStorageAccount storageAccount = CloudStorageAccount.Parse(storageConnectionString);
            CloudTableClient tableClient = storageAccount.CreateCloudTableClient();
            CloudTable table = tableClient.GetTableReference(tableName);
            await table.CreateIfNotExistsAsync();
            return table;
        }

        public static async Task<TableStorageResponse<TResult>> DoTableInsertOrReplaceAsync<TResult, TInput>(TInput incomingEntity,
            Func<TInput, TResult> tableEntityToModelConverter, string storageAccountConnectionString, string tableName) where TInput : TableEntity
        {
            var table = await GetTableAsync(storageAccountConnectionString, tableName);

            // Simply doing an InsertOrReplace will not do any concurrency checking, according to 
            // http://azure.microsoft.com/en-us/blog/managing-concurrency-in-microsoft-azure-storage-2/
            // So we will not use InsertOrReplace. Instead we will look to see if we have a rule like this
            // If so, then we'll do a concurrency-safe update, otherwise simply insert
            TableOperation retrieveOperation =
                TableOperation.Retrieve<TInput>(incomingEntity.PartitionKey, incomingEntity.RowKey);
            TableResult retrievedEntity = await table.ExecuteAsync(retrieveOperation);

            TableOperation operation = null;
            if (retrievedEntity.Result != null)
            {
                operation = TableOperation.Replace(incomingEntity);
            }
            else
            {
                operation = TableOperation.Insert(incomingEntity);
            }

            return await PerformTableOperation(table, operation, incomingEntity, tableEntityToModelConverter);
        }

        public static async Task<TableStorageResponse<TResult>> DoDeleteAsync<TResult, TInput>(TInput incomingEntity,
            Func<TInput, TResult> tableEntityToModelConverter, string storageAccountConnectionString, string tableName) where TInput : TableEntity
        {
            var azureTable = await GetTableAsync(storageAccountConnectionString, tableName);
            TableOperation operation = TableOperation.Delete(incomingEntity);
            return await PerformTableOperation(azureTable, operation, incomingEntity, tableEntityToModelConverter);
        }

        static async Task<TableStorageResponse<TResult>> PerformTableOperation<TResult, TInput>(CloudTable table,
            TableOperation operation, TInput incomingEntity, Func<TInput, TResult> tableEntityToModelConverter) where TInput : TableEntity
        {
            var result = new TableStorageResponse<TResult>();

            try
            {
                await table.ExecuteAsync(operation);

                var nullModel = tableEntityToModelConverter(null);
                result.Entity = nullModel;
                result.Status = TableStorageResponseStatus.Successful;
            }
            catch (Exception ex)
            {
                TableOperation retrieveOperation = TableOperation.Retrieve<TInput>(incomingEntity.PartitionKey, incomingEntity.RowKey);
                TableResult retrievedEntity = table.Execute(retrieveOperation);

                if (retrievedEntity != null)
                {
                    // Return the found version of this rule in case it had been modified by someone else since our last read.
                    var retrievedModel = tableEntityToModelConverter((TInput)retrievedEntity.Result);
                    result.Entity = retrievedModel;
                }
                else
                {
                    // We didn't find an existing rule, probably creating new, so we'll just return what was sent in
                    result.Entity = tableEntityToModelConverter(incomingEntity);
                }

                if (ex.GetType() == typeof(StorageException)
                    && (((StorageException)ex).RequestInformation.HttpStatusCode == (int)HttpStatusCode.PreconditionFailed
                        || ((StorageException)ex).RequestInformation.HttpStatusCode == (int)HttpStatusCode.Conflict))
                {
                    result.Status = TableStorageResponseStatus.ConflictError;
                }
                else
                {
                    result.Status = TableStorageResponseStatus.UnknownError;
                }
            }

            return result;
        }
    }
}