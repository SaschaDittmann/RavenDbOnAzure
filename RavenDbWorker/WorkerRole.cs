using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using Microsoft.WindowsAzure;
using Microsoft.WindowsAzure.ServiceRuntime;
using Microsoft.WindowsAzure.StorageClient;
using Raven.Abstractions.Data;
using Raven.Database;
using Raven.Database.Config;
using Raven.Database.Server;
using Raven.Json.Linq;

namespace RavenDbWorker
{
    public class WorkerRole : RoleEntryPoint
    {
        #region Private Fields

        private CloudDrive _dataDrive;
        private DocumentDatabase _database;
        private HttpServer _server;

        #endregion

        #region RoleEntryPoint Methods & Event Handlers

        public override bool OnStart()
        {
            try
            {
                CloudStorageAccount.SetConfigurationSettingPublisher(
                    (configName, configSetter) =>
                        configSetter(RoleEnvironment.GetConfigurationSettingValue(configName)));

                SetupTraceListener();

                // Set the maximum number of concurrent connections 
                ServicePointManager.DefaultConnectionLimit = 12;

                MountCloudDrive();

                StartRaven();
                SetupReplication();

                RoleEnvironment.Changed += RoleEnvironmentChanged;
            }
            catch (Exception ex)
            {
                Trace.TraceError("OnStart Error: {0}", ex.Message);
            }

            return base.OnStart();
        }

        public override void Run()
        {
            Trace.TraceInformation("RavenDb: Running");

            while (true)
            {
                Thread.Sleep(10000);
                //TODO: Check RavenDb State
            }
            // ReSharper disable FunctionNeverReturns
        }
        // ReSharper restore FunctionNeverReturns

        public override void OnStop()
        {
            try
            {
                StopRaven();
            }
            catch (Exception ex)
            {
                Trace.TraceError("OnStop Error: {0}", ex.Message);
            }

            base.OnStop();
        }

        private void RoleEnvironmentChanged(object sender, RoleEnvironmentChangedEventArgs e)
        {
            try
            {
                Trace.TraceInformation("Event: Role Environment Changed.");

                if (e.Changes.OfType<RoleEnvironmentTopologyChange>().Any())
                {
                    SetupReplication();
                }
            }
            catch (Exception ex)
            {
                Trace.TraceError("RoleEnvironmentChanged Error: {0}", ex.Message);
            }
        }

        #endregion

        #region RavenDB Methods

        private void StartRaven()
        {
            try
            {
                Trace.TraceInformation("RavenDb: Starting...");

                AnonymousUserAccessMode anonymousUserAccessMode;
                if (!Enum.TryParse(RoleEnvironment.GetConfigurationSettingValue("AnonymousUserAccessMode"), true, out anonymousUserAccessMode))
                    anonymousUserAccessMode = AnonymousUserAccessMode.Get;
                Trace.TraceInformation("Raven Configuration AnonymousUserAccessMode: {0}", anonymousUserAccessMode);

                var httpCompression = Boolean.Parse(RoleEnvironment.GetConfigurationSettingValue("HttpCompression"));
                Trace.TraceInformation("Raven Configuration HttpCompression: {0}", httpCompression);

                var defaultStorageTypeName = RoleEnvironment.GetConfigurationSettingValue("DefaultStorageTypeName");
                Trace.TraceInformation("Raven Configuration DefaultStorageTypeName: {0}", defaultStorageTypeName);

                var port = RoleEnvironment.CurrentRoleInstance.InstanceEndpoints["Raven"].IPEndpoint.Port;
                Trace.TraceInformation("Raven Configuration Port: {0}", port);

                Trace.TraceInformation("RavenDb: Ensure Can ListenTo When In Non Admin Context...");
                NonAdminHttp.EnsureCanListenToWhenInNonAdminContext(port);

                var config = new RavenConfiguration
                {
                    DataDirectory = _dataDrive.LocalPath.EndsWith("\\")
                                        ? _dataDrive.LocalPath + "Data\\"
                                        : _dataDrive.LocalPath + "\\Data\\",
                    AnonymousUserAccessMode = anonymousUserAccessMode,
                    HttpCompression = httpCompression,
                    DefaultStorageTypeName = defaultStorageTypeName,
                    Port = port,
                    PluginsDirectory = "Plugins"
                };
                _database = new DocumentDatabase(config);

                Trace.TraceInformation("RavenDb: Spin Background Workers...");
                _database.SpinBackgroundWorkers();

                _server = new HttpServer(config, _database);
                try
                {
                    Trace.TraceInformation("Http Server: Initializing ...");
                    _server.Init();
                    Trace.TraceInformation("Http Server: Start Listening ...");
                    _server.StartListening();
                }
                catch (Exception)
                {
                    _server.Dispose();
                    _server = null;
                    throw;
                }

                Trace.TraceInformation("RavenDb: Started.");
            }
            catch (Exception)
            {
                if (_database != null)
                {
                    _database.Dispose();
                    _database = null;
                }
                throw;
            }
        }

        private void StopRaven()
        {
            Trace.TraceWarning("RavenDb: Stopping...");
            if (_server != null)
                _server.Dispose();
            if (_database != null)
                _database.Dispose();
            if (_dataDrive != null)
                _dataDrive.Unmount();
            Trace.TraceWarning("RavenDb: Stopped.");
        }

        private void SetupReplication()
        {
            Trace.TraceInformation("RavenDb: Setup Replication...");

            var json = GetReplicationDestinations();
            Trace.TraceInformation("RavenDb Replication Destinations: {0}", json);

            var tr = new TransactionInformation();
            _database.Delete("Raven/Replication/Destinations", null, tr);
            _database.Put("Raven/Replication/Destinations",
                            null,
                            RavenJObject.Parse(json),
                            new RavenJObject(), tr);
            _database.Commit(tr.Id);

            Trace.TraceInformation("RavenDb: Setup Replication Completed.");
        }

        private static string GetReplicationDestinations()
        {
            var json = new StringBuilder(@"{""Id"":""Raven/Replication/Destinations"",""Destinations"":[");
            foreach (var roleInstance in RoleEnvironment.CurrentRoleInstance.Role.Instances
                .Where(instance => instance.Id != RoleEnvironment.CurrentRoleInstance.Id))
            {
                RoleInstanceEndpoint endpoint;
                if (roleInstance.InstanceEndpoints.TryGetValue("Replication", out endpoint))
                {
                    json.AppendFormat(
                        @"{{""Url"":""{0}""}}",
                        string.Format("http://{0}:{1}/",
                            endpoint.IPEndpoint.Address,
                            RoleEnvironment.CurrentRoleInstance.InstanceEndpoints["Raven"].IPEndpoint.Port));
                }
                else
                {
                    Trace.TraceError(
                        "Error: Unable to retrieve the Replication endpoint for Role Instance {0}.",
                        roleInstance.Id);
                }
            }
            json.Append("]}");
            return json.ToString();
        }

        #endregion

        #region Trace Listener & Cloud Drive

        private static void SetupTraceListener()
        {
            bool enableTraceListener;
            var enableTraceListenerSetting = RoleEnvironment.GetConfigurationSettingValue("EnableTableStorageTraceListener");
            if (bool.TryParse(enableTraceListenerSetting, out enableTraceListener))
            {
                if (enableTraceListener)
                {
                    var listener =
                        new AzureDiagnostics.TableStorageTraceListener("StorageAccount")
                        {
                            Name = "TableStorageTraceListener"
                        };
                    Trace.Listeners.Add(listener);
                    Trace.AutoFlush = true;
                    Trace.TraceInformation("TableStorageTraceListener is enabled.");
                }
                else
                {
                    Trace.TraceInformation("Removing TableStorageTraceListener...");
                    Trace.Listeners.Remove("TableStorageTraceListener");
                }
            }
        }

        private void MountCloudDrive()
        {
            Trace.TraceInformation("Cloud Drive: Mounting...");

            CloudStorageAccount storageAccount = CloudStorageAccount.FromConfigurationSetting("StorageAccount");
            CloudBlobClient blobClient = storageAccount.CreateCloudBlobClient();

            LocalResource localCache = RoleEnvironment.GetLocalResource("RavenCache");
            CloudDrive.InitializeCache(localCache.RootPath.TrimEnd('\\'), localCache.MaximumSizeInMegabytes);

            var containerName = RoleEnvironment.GetConfigurationSettingValue("CloudDriveContainer");
            Trace.TraceInformation("Cloud Drive: Container = {0}", containerName);
            blobClient.GetContainerReference(containerName).CreateIfNotExist();

            var vhdName = RoleEnvironment.CurrentRoleInstance.Id + ".vhd";
            Trace.TraceInformation("Cloud Drive: VHD = {0}", vhdName);

            var vhdUrl = blobClient.GetContainerReference(containerName).GetPageBlobReference(vhdName).Uri.ToString();
            _dataDrive = storageAccount.CreateCloudDrive(vhdUrl);
            _dataDrive.CreateIfNotExist(localCache.MaximumSizeInMegabytes);

            var localPath = _dataDrive.Mount(localCache.MaximumSizeInMegabytes, DriveMountOptions.Force);
            Trace.TraceInformation("Cloud Drive mounted to {0}", localPath);
        }

        #endregion
    }
}