﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.Data.SqlClient;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using Microsoft.Extensions.Logging;
using Microsoft.Health.Fhir.Core;
using Microsoft.Health.Fhir.Core.Features.Operations;
using Microsoft.Health.Fhir.Core.Features.Operations.Export.Models;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.SqlServer.Features.Schema.Model;
using Newtonsoft.Json;

namespace Microsoft.Health.Fhir.SqlServer.Features.Storage
{
    internal class SqlServerFhirOperationDataStore : IFhirOperationDataStore
    {
        private readonly SqlConnectionWrapperFactory _sqlConnectionWrapperFactory;
        private readonly ILogger<SqlServerFhirOperationDataStore> _logger;

        public SqlServerFhirOperationDataStore(
            SqlConnectionWrapperFactory sqlConnectionWrapperFactory,
            ILogger<SqlServerFhirOperationDataStore> logger)
        {
            EnsureArg.IsNotNull(sqlConnectionWrapperFactory, nameof(sqlConnectionWrapperFactory));
            EnsureArg.IsNotNull(logger, nameof(logger));

            _sqlConnectionWrapperFactory = sqlConnectionWrapperFactory;
            _logger = logger;
        }

        public async Task<ExportJobOutcome> CreateExportJobAsync(ExportJobRecord jobRecord, CancellationToken cancellationToken)
        {
            using (SqlConnectionWrapper sqlConnectionWrapper = _sqlConnectionWrapperFactory.ObtainSqlConnectionWrapper(true))
            using (SqlCommand sqlCommand = sqlConnectionWrapper.CreateSqlCommand())
            {
                V1.CreateExportJob.PopulateCommand(
                    sqlCommand,
                    jobRecord.Id,
                    jobRecord.Hash,
                    jobRecord.Status.ToString(),
                    jobRecord.QueuedTime,
                    JsonConvert.SerializeObject(jobRecord));

                var rowVersion = (int?)await sqlCommand.ExecuteScalarAsync(cancellationToken);

                if (rowVersion == null)
                {
                    throw new OperationFailedException(string.Format(Core.Resources.OperationFailed, OperationsConstants.Export, Resources.NullRowVersion), HttpStatusCode.InternalServerError);
                }

                return new ExportJobOutcome(jobRecord, WeakETag.FromVersionId(rowVersion.ToString()));
            }
        }

        public async Task<ExportJobOutcome> GetExportJobByIdAsync(string id, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNullOrWhiteSpace(id, nameof(id));

            using (SqlConnectionWrapper sqlConnectionWrapper = _sqlConnectionWrapperFactory.ObtainSqlConnectionWrapper(true))
            using (SqlCommand sqlCommand = sqlConnectionWrapper.CreateSqlCommand())
            {
                V1.GetExportJobById.PopulateCommand(sqlCommand, id);

                using (SqlDataReader sqlDataReader = await sqlCommand.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken))
                {
                    if (!sqlDataReader.Read())
                    {
                        throw new JobNotFoundException(string.Format(Core.Resources.JobNotFound, id));
                    }

                    (string rawJobRecord, byte[] rowVersion) = sqlDataReader.ReadRow(V1.ExportJob.RawJobRecord, V1.ExportJob.JobVersion);

                    return CreateExportJobOutcome(rawJobRecord, rowVersion);
                }
            }
        }

        public async Task<ExportJobOutcome> GetExportJobByHashAsync(string hash, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNullOrWhiteSpace(hash, nameof(hash));

            using (SqlConnectionWrapper sqlConnectionWrapper = _sqlConnectionWrapperFactory.ObtainSqlConnectionWrapper(true))
            using (SqlCommand sqlCommand = sqlConnectionWrapper.CreateSqlCommand())
            {
                V1.GetExportJobByHash.PopulateCommand(sqlCommand, hash);

                using (SqlDataReader sqlDataReader = await sqlCommand.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken))
                {
                    if (!sqlDataReader.Read())
                    {
                        return null;
                    }

                    (string rawJobRecord, byte[] rowVersion) = sqlDataReader.ReadRow(V1.ExportJob.RawJobRecord, V1.ExportJob.JobVersion);

                    return CreateExportJobOutcome(rawJobRecord, rowVersion);
                }
            }
        }

        public async Task<ExportJobOutcome> UpdateExportJobAsync(ExportJobRecord jobRecord, WeakETag eTag, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(jobRecord, nameof(jobRecord));

            using (SqlConnectionWrapper sqlConnectionWrapper = _sqlConnectionWrapperFactory.ObtainSqlConnectionWrapper(true))
            using (SqlCommand sqlCommand = sqlConnectionWrapper.CreateSqlCommand())
            {
                // We will timestamp the jobs when we update them to track stale jobs.
                DateTimeOffset heartbeatTimeStamp = Clock.UtcNow;

                V1.UpdateExportJob.PopulateCommand(
                    sqlCommand,
                    jobRecord.Id,
                    jobRecord.Status.ToString(),
                    heartbeatTimeStamp,
                    jobRecord.QueuedTime,
                    JsonConvert.SerializeObject(jobRecord));

                var rowVersion = (int?)await sqlCommand.ExecuteScalarAsync(cancellationToken);

                if (rowVersion == null)
                {
                    throw new OperationFailedException(string.Format(Core.Resources.OperationFailed, OperationsConstants.Export, Resources.NullRowVersion), HttpStatusCode.InternalServerError);
                }

                return new ExportJobOutcome(jobRecord, WeakETag.FromVersionId(rowVersion.ToString()));
            }
        }

        public async Task<IReadOnlyCollection<ExportJobOutcome>> AcquireExportJobsAsync(ushort maximumNumberOfConcurrentJobsAllowed, TimeSpan jobHeartbeatTimeoutThreshold, CancellationToken cancellationToken)
        {
            // We will consider a job to be stale if its timestamp is smaller than or equal to this.
            DateTimeOffset expirationTime = Clock.UtcNow - jobHeartbeatTimeoutThreshold;

            // We will timestamp the jobs when we mark them as running to track stale jobs.
            DateTimeOffset heartbeatTimeStamp = Clock.UtcNow;

            using (SqlConnectionWrapper sqlConnectionWrapper = _sqlConnectionWrapperFactory.ObtainSqlConnectionWrapper(true))
            using (SqlCommand sqlCommand = sqlConnectionWrapper.CreateSqlCommand())
            {
                V1.AcquireExportJobs.PopulateCommand(
                    sqlCommand,
                    expirationTime,
                    maximumNumberOfConcurrentJobsAllowed,
                    heartbeatTimeStamp);

                var acquiredJobs = new List<ExportJobOutcome>();

                using (SqlDataReader sqlDataReader = await sqlCommand.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken))
                {
                    while (await sqlDataReader.ReadAsync(cancellationToken))
                    {
                        (string rawJobRecord, byte[] rowVersion) = sqlDataReader.ReadRow(V1.ExportJob.RawJobRecord, V1.ExportJob.JobVersion);

                        acquiredJobs.Add(CreateExportJobOutcome(rawJobRecord, rowVersion));
                    }
                }

                return new ReadOnlyCollection<ExportJobOutcome>(acquiredJobs);
            }
        }

        private static ExportJobOutcome CreateExportJobOutcome(string rawJobRecord, byte[] rowVersionAsBytes)
        {
            var exportJobRecord = JsonConvert.DeserializeObject<ExportJobRecord>(rawJobRecord);

            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(rowVersionAsBytes);
            }

            const int startIndex = 0;
            var rowVersionAsDecimalString = BitConverter.ToInt64(rowVersionAsBytes, startIndex).ToString();

            return new ExportJobOutcome(exportJobRecord, WeakETag.FromVersionId(rowVersionAsDecimalString));
        }
    }
}
