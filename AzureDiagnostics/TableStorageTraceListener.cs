// ----------------------------------------------------------------------------------
// Microsoft Developer & Platform Evangelism
// 
// Copyright (c) Microsoft Corporation. All rights reserved.
// 
// THIS CODE AND INFORMATION ARE PROVIDED "AS IS" WITHOUT WARRANTY OF ANY KIND, 
// EITHER EXPRESSED OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE IMPLIED WARRANTIES 
// OF MERCHANTABILITY AND/OR FITNESS FOR A PARTICULAR PURPOSE.
// ----------------------------------------------------------------------------------
// The example companies, organizations, products, domain names,
// e-mail addresses, logos, people, places, and events depicted
// herein are fictitious.  No association with any real company,
// organization, product, domain name, email address, logo, person,
// places, or events is intended or should be inferred.
// ----------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Text;
using System.Diagnostics;
using Microsoft.WindowsAzure;
using Microsoft.WindowsAzure.ServiceRuntime;
using Microsoft.WindowsAzure.StorageClient;
using System.Data.Services.Client;

namespace AzureDiagnostics
{
    public class TableStorageTraceListener : TraceListener
    {
        private const string DEFAULT_DIAGNOSTICS_CONNECTION_STRING = "Microsoft.WindowsAzure.Plugins.Diagnostics.ConnectionString";

        public static readonly string DIAGNOSTICS_TABLE = "DevLogsTable";

        [ThreadStatic]
        private static StringBuilder messageBuffer;

        private object initializationSection = new object();
        private bool isInitialized = false;

        private object traceLogAccess = new object();
        private List<LogEntry> traceLog = new List<LogEntry>();

        private CloudTableClient tableStorage;
        private string connectionString;

        public TableStorageTraceListener()
            : this(DEFAULT_DIAGNOSTICS_CONNECTION_STRING)
        {
        }

        public TableStorageTraceListener(string connectionString)
        {
            this.connectionString = connectionString;
        }

        public override bool IsThreadSafe
        {
            get
            {
                return true;
            }
        }

        public override void Write(string message)
        {
            if (TableStorageTraceListener.messageBuffer == null)
            {
                TableStorageTraceListener.messageBuffer = new StringBuilder();
            }

            TableStorageTraceListener.messageBuffer.Append(message);
        }

        public override void WriteLine(string message)
        {
            if (TableStorageTraceListener.messageBuffer == null)
            {
                TableStorageTraceListener.messageBuffer = new StringBuilder();
            }

            TableStorageTraceListener.messageBuffer.AppendLine(message);
        }

        public override void Flush()
        {
            if (!this.isInitialized)
            {
                lock (this.initializationSection)
                {
                    if (!this.isInitialized)
                    {
                        Initialize();
                    }
                }
            }

            TableServiceContext context = tableStorage.GetDataServiceContext();
            context.MergeOption = MergeOption.AppendOnly;
            lock (this.traceLogAccess)
            {
                this.traceLog.ForEach(entry => context.AddObject(DIAGNOSTICS_TABLE, entry));
                this.traceLog.Clear();
            }

            if (context.Entities.Count > 0)
            {
                context.BeginSaveChangesWithRetries(SaveChangesOptions.None, (ar) =>
                {
                    context.EndSaveChangesWithRetries(ar);
                }, null);
            }
        }

        public override void TraceData(TraceEventCache eventCache, string source, TraceEventType eventType, int id, object data)
        {
            base.TraceData(eventCache, source, eventType, id, data);
            AppendEntry(id, eventType, eventCache);
        }

        public override void TraceData(TraceEventCache eventCache, string source, TraceEventType eventType, int id, params object[] data)
        {
            base.TraceData(eventCache, source, eventType, id, data);
            AppendEntry(id, eventType, eventCache);
        }

        public override void TraceEvent(TraceEventCache eventCache, string source, TraceEventType eventType, int id)
        {
            base.TraceEvent(eventCache, source, eventType, id);
            AppendEntry(id, eventType, eventCache);
        }

        public override void TraceEvent(TraceEventCache eventCache, string source, TraceEventType eventType, int id, string format, params object[] args)
        {
            base.TraceEvent(eventCache, source, eventType, id, format, args);
            AppendEntry(id, eventType, eventCache);
        }

        public override void TraceEvent(TraceEventCache eventCache, string source, TraceEventType eventType, int id, string message)
        {
            base.TraceEvent(eventCache, source, eventType, id, message);
            AppendEntry(id, eventType, eventCache);
        }

        public override void TraceTransfer(TraceEventCache eventCache, string source, int id, string message, Guid relatedActivityId)
        {
            base.TraceTransfer(eventCache, source, id, message, relatedActivityId);
            AppendEntry(id, TraceEventType.Transfer, eventCache);
        }

        private void Initialize()
        {
            CloudStorageAccount account = CloudStorageAccount.FromConfigurationSetting(this.connectionString);
            this.tableStorage = account.CreateCloudTableClient();
            this.tableStorage.CreateTableIfNotExist(DIAGNOSTICS_TABLE);
            this.isInitialized = true;
        }

        private void AppendEntry(int id, TraceEventType eventType, TraceEventCache eventCache)
        {
            if (TableStorageTraceListener.messageBuffer == null)
            {
                TableStorageTraceListener.messageBuffer = new StringBuilder();
            }

            string message = TableStorageTraceListener.messageBuffer.ToString();
            TableStorageTraceListener.messageBuffer.Length = 0;

            if (message.EndsWith(Environment.NewLine))
            {
                message = message.Substring(0, message.Length - Environment.NewLine.Length);
            }

            if (message.Length == 0)
            {
                return;
            }

            LogEntry entry = new LogEntry()
            {
                PartitionKey = string.Format("{0:D10}", eventCache.Timestamp >> 30),
                RowKey = string.Format("{0:D19}", eventCache.Timestamp),
                EventTickCount = eventCache.Timestamp,
                Level = (int)eventType,
                EventId = id,
                Pid = eventCache.ProcessId,
                Tid = eventCache.ThreadId,
                RoleName = RoleEnvironment.CurrentRoleInstance.Role.Name,
                RoleId = RoleEnvironment.CurrentRoleInstance.Id,
                Message = message
            };

            lock (this.traceLogAccess)
            {
                this.traceLog.Add(entry);
            }
        }
    }
}
