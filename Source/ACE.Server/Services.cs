using ACE.Common;
using ACE.Database;
using ACE.Database.Models.Auth;
using ACE.Database.Models.Shard;
using ACE.Database.Models.World;
using ACE.DatLoader;
using ACE.Server.Command;
using ACE.Server.Managers;
using ACE.Server.Mods;
using ACE.Server.Network.Managers;
using log4net;
using log4net.Config;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Runtime;

namespace ACE.Server
{
    public static partial class Services
    {
        public interface IACRealmsService
        {
            bool IsDisposed { get; }
        }

        /// <summary>
        /// The timeBeginPeriod function sets the minimum timer resolution for an application or device driver. Used to manipulate the timer frequency.
        /// https://docs.microsoft.com/en-us/windows/desktop/api/timeapi/nf-timeapi-timebeginperiod
        /// Important note: This function affects a global Windows setting. Windows uses the lowest value (that is, highest resolution) requested by any process.
        /// </summary>
        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        public static extern uint MM_BeginPeriod(uint uMilliseconds);

        /// <summary>
        /// The timeEndPeriod function clears a previously set minimum timer resolution
        /// https://docs.microsoft.com/en-us/windows/desktop/api/timeapi/nf-timeapi-timeendperiod
        /// </summary>
        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        public static extern uint MM_EndPeriod(uint uMilliseconds);

        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public static void ConfigureServicesForLiveEnvironment()
        {
            var consoleTitle = $"AC Realms - v{ServerBuildInfo.FullVersion}";
            Console.Title = consoleTitle;

            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            AppDomain.CurrentDomain.ProcessExit += new EventHandler(OnProcessExit);

            // Typically, you wouldn't force the current culture on an entire application unless you know sure your application is used in a specific region (which ACE is not)
            // We do this because almost all of the client/user input/output code does not take culture into account, and assumes en-US formatting.
            // Without this, many commands that require special characters like , and . will break
            Thread.CurrentThread.CurrentCulture = new CultureInfo("en-US");
            // Init our text encoding options. This will allow us to use more than standard ANSI text, which the client also supports.
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

            // Look for the log4net.config first in the current environment directory, then in the ExecutingAssembly location
            var exeLocation = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var containerConfigDirectory = "/ace/Config";
            var log4netConfig = Path.Combine(exeLocation, "log4net.config");
            var log4netConfigExample = Path.Combine(exeLocation, "log4net.config.example");
            var log4netConfigContainer = Path.Combine(containerConfigDirectory, "log4net.config");

            if (Program.IsRunningInContainer && File.Exists(log4netConfigContainer))
                File.Copy(log4netConfigContainer, log4netConfig, true);

            var log4netFileInfo = new FileInfo("log4net.config");
            if (!log4netFileInfo.Exists)
                log4netFileInfo = new FileInfo(log4netConfig);

            if (!log4netFileInfo.Exists)
            {
                var exampleFile = new FileInfo(log4netConfigExample);
                if (!exampleFile.Exists)
                {
                    Console.WriteLine("log4net Configuration file is missing.  Please copy the file log4net.config.example to log4net.config and edit it to match your needs before running AC Realms.");
                    throw new Exception("missing log4net configuration file");
                }
                else
                {
                    if (!Program.IsRunningInContainer)
                    {
                        Console.WriteLine("log4net Configuration file is missing,  cloning from example file.");
                        File.Copy(log4netConfigExample, log4netConfig);
                    }
                    else
                    {
                        if (!File.Exists(log4netConfigContainer))
                        {
                            Console.WriteLine("log4net Configuration file is missing, AC Realms Emulator is running in a container,  cloning from docker file.");
                            var log4netConfigDocker = Path.Combine(exeLocation, "log4net.config.docker");
                            File.Copy(log4netConfigDocker, log4netConfig);
                            File.Copy(log4netConfigDocker, log4netConfigContainer);
                        }
                        else
                        {
                            File.Copy(log4netConfigContainer, log4netConfig);
                        }

                    }
                }
            }

            var logRepository = LogManager.GetRepository(Assembly.GetEntryAssembly());
            XmlConfigurator.ConfigureAndWatch(logRepository, log4netFileInfo);

            if (Environment.ProcessorCount < 2)
                log.Warn("Only one vCPU was detected. AC Realms may run with limited performance. You should increase your vCPU count for anything more than a single player server.");

            // Do system specific initializations here
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    // On many windows systems, the default resolution for Thread.Sleep is 15.6ms. This allows us to command a tighter resolution
                    MM_BeginPeriod(1);
                }
            }
            catch (Exception ex)
            {
                log.Error(ex.ToString());
            }

            log.Info("Starting AC Realms...");

            if (Program.IsRunningInContainer)
                log.Info("AC Realms is running in a container...");



            var configFile = Path.Combine(exeLocation, "Config.js");
            var configConfigContainer = Path.Combine(containerConfigDirectory, "Config.js");

            if (Program.IsRunningInContainer && File.Exists(configConfigContainer))
                File.Copy(configConfigContainer, configFile, true);

            if (!File.Exists(configFile))
            {
                if (!Program.IsRunningInContainer)
                    DoOutOfBoxSetup(configFile);
                else
                {
                    if (!File.Exists(configConfigContainer))
                    {
                        DoOutOfBoxSetup(configFile);
                        File.Copy(configFile, configConfigContainer);
                    }
                    else
                        File.Copy(configConfigContainer, configFile);
                }
            }

            log.Info("Initializing ConfigManager...");
            ConfigManager.Initialize();

            log.Info("Initializing ModManager...");
            ModManager.Initialize();

            if (ConfigManager.Config.Server.WorldName != "AC Realms")
            {
                consoleTitle = $"{ConfigManager.Config.Server.WorldName} | {consoleTitle}";
                Console.Title = consoleTitle;
            }

            // https://learn.microsoft.com/en-us/dotnet/core/extensions/generic-host?tabs=appbuilder
            var builder = Host.CreateDefaultBuilder();
            builder.ConfigureServices((context, services) =>
            {
                // Disable logging of db commands
                var dbLogger = LoggerFactory.Create(builder => builder.AddFilter(_ => false));

                services.AddDbContextFactory<AuthDbContext>(options =>
                {
                    options.UseLoggerFactory(dbLogger);
                    var config = ConfigManager.Config.MySql.Authentication;
                    var connectionString = $"server={config.Host};port={config.Port};user={config.Username};password={config.Password};database={config.Database};{config.ConnectionOptions}";
                    options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString), builder =>
                    {
                        builder.EnableRetryOnFailure(10);
                    });
                });
                services.AddDbContextFactory<WorldDbContext>(options =>
                {
                    options.UseLoggerFactory(dbLogger);
                    var config = ConfigManager.Config.MySql.World;
                    var connectionString = $"server={config.Host};port={config.Port};user={config.Username};password={config.Password};database={config.Database};{config.ConnectionOptions}";
                    options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString), builder =>
                    {
                        builder.EnableRetryOnFailure(10);
                    });
                });
                services.AddDbContextFactory<ShardDbContext>(options =>
                {
                    options.UseLoggerFactory(dbLogger);
                    var config = ConfigManager.Config.MySql.Shard;
                    var connectionString = $"server={config.Host};port={config.Port};user={config.Username};password={config.Password};database={config.Database};{config.ConnectionOptions}";
                    options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString), builder =>
                    {
                        builder.EnableRetryOnFailure(10);
                    });
                });
            });
            var host = builder.Build();
            var services = host.Services;

            if (ConfigManager.Config.Offline.PurgeDeletedCharacters)
            {
                log.Info($"Purging deleted characters, and their possessions, older than {ConfigManager.Config.Offline.PurgeDeletedCharactersDays} days ({DateTime.Now.AddDays(-ConfigManager.Config.Offline.PurgeDeletedCharactersDays)})...");
                ShardDatabaseOfflineTools.PurgeCharactersInParallel(ConfigManager.Config.Offline.PurgeDeletedCharactersDays, out var charactersPurged, out var playerBiotasPurged, out var possessionsPurged);
                log.Info($"Purged {charactersPurged:N0} characters, {playerBiotasPurged:N0} player biotas and {possessionsPurged:N0} possessions.");
            }

            if (ConfigManager.Config.Offline.PurgeOrphanedBiotas)
            {
                log.Info($"Purging orphaned biotas...");
                ShardDatabaseOfflineTools.PurgeOrphanedBiotasInParallel(out var numberOfBiotasPurged);
                log.Info($"Purged {numberOfBiotasPurged:N0} biotas.");
            }

            if (ConfigManager.Config.Offline.PruneDeletedCharactersFromFriendLists)
            {
                log.Info($"Pruning invalid friends from all friend lists...");
                ShardDatabaseOfflineTools.PruneDeletedCharactersFromFriendLists(out var numberOfFriendsPruned);
                log.Info($"Pruned {numberOfFriendsPruned:N0} invalid friends found on friend lists.");
            }

            if (ConfigManager.Config.Offline.PruneDeletedObjectsFromShortcutBars)
            {
                log.Info($"Pruning invalid shortcuts from all shortcut bars...");
                ShardDatabaseOfflineTools.PruneDeletedObjectsFromShortcutBars(out var numberOfShortcutsPruned);
                log.Info($"Pruned {numberOfShortcutsPruned:N0} deleted objects found on shortcut bars.");
            }

            if (ConfigManager.Config.Offline.PruneDeletedCharactersFromSquelchLists)
            {
                log.Info($"Pruning invalid squelches from all squelch lists...");
                ShardDatabaseOfflineTools.PruneDeletedCharactersFromSquelchLists(out var numberOfSquelchesPruned);
                log.Info($"Pruned {numberOfSquelchesPruned:N0} invalid squelched characters found on squelch lists.");
            }

            if (ConfigManager.Config.Offline.AutoServerUpdateCheck)
                CheckForServerUpdate();
            else
                log.Info($"AutoServerVersionCheck is disabled...");

            if (ConfigManager.Config.Offline.AutoUpdateWorldDatabase)
            {
                CheckForWorldDatabaseUpdate();

                if (ConfigManager.Config.Offline.AutoApplyWorldCustomizations)
                    AutoApplyWorldCustomizations();
            }
            else
                log.Info($"AutoUpdateWorldDatabase is disabled...");

            if (ConfigManager.Config.Offline.AutoApplyDatabaseUpdates)
                AutoApplyDatabaseUpdates();
            else
                log.Info($"AutoApplyDatabaseUpdates is disabled...");

            // This should only be enabled manually. To enable it, simply uncomment this line
            //ACE.Database.OfflineTools.Shard.BiotaGuidConsolidator.ConsolidateBiotaGuids(0xA0000000, true, false, out int numberOfBiotasConsolidated, out int numberOfBiotasSkipped, out int numberOfErrors);
            //ACE.Database.OfflineTools.Shard.BiotaGuidConsolidator.ConsolidateBiotaGuids(0xD0000000, false, true, out int numberOfBiotasConsolidated2, out int numberOfBiotasSkipped2, out int numberOfErrors2);

            ShardDatabaseOfflineTools.CheckForBiotaPropertiesPaletteOrderColumnInShard();

            // pre-load starterGear.json, abort startup if file is not found as it is required to create new characters.
            if (Factories.StarterGearFactory.GetStarterGearConfiguration() == null)
            {
                log.Fatal("Unable to load or parse starterGear.json. AC Realms will now abort startup.");
                ServerManager.StartupAbort();
                Environment.Exit(0);
            }

            log.Info("Initializing ServerManager...");
            ServerManager.Initialize();

            log.Info("Initializing DatManager...");
            DatManager.Initialize(ConfigManager.Config.Server.DatFilesDirectory, true);
            Physics.Common.LandDefs.LandHeightTable = DatManager.PortalDat.RegionDesc.LandDefs.LandHeightTable;

            if (ConfigManager.Config.DDD.EnableDATPatching)
            {
                log.Info("Initializing DDDManager...");
                DDDManager.Initialize();
            }
            else
                log.Info("DAT Patching Disabled...");

            log.Info("Initializing DatabaseManager...");
            DatabaseManager.Initialize(services);


            if (DatabaseManager.InitializationFailure)
            {
                log.Fatal("DatabaseManager initialization failed. AC Realms will now abort startup.");
                ServerManager.StartupAbort();
                Environment.Exit(0);
            }

            log.Info("Starting DatabaseManager...");
            DatabaseManager.Start();

            log.Info("Starting PropertyManager...");
            PropertyManager.Initialize();

            log.Info("Initializing GuidManager...");
            GuidManager.Initialize();

            if (ConfigManager.Config.Server.ServerPerformanceMonitorAutoStart)
            {
                log.Info("Server Performance Monitor auto starting...");
                ServerPerformanceMonitor.Start();
            }

            if (ConfigManager.Config.Server.WorldDatabasePrecaching)
            {
                log.Info("Precaching Weenies...");
                DatabaseManager.World.CacheAllWeenies();
                log.Info("Precaching Cookbooks...");
                DatabaseManager.World.CacheAllCookbooks();
                log.Info("Precaching Events...");
                DatabaseManager.World.GetAllEvents();
                log.Info("Precaching House Portals...");
                DatabaseManager.World.CacheAllHousePortals();
                log.Info("Precaching Points Of Interest...");
                DatabaseManager.World.CacheAllPointsOfInterest();
                log.Info("Precaching Spells...");
                DatabaseManager.World.CacheAllSpells();
                log.Info("Precaching Treasures - Death...");
                DatabaseManager.World.CacheAllTreasuresDeath();
                log.Info("Precaching Treasures - Material Base...");
                DatabaseManager.World.CacheAllTreasureMaterialBase();
                log.Info("Precaching Treasures - Material Groups...");
                DatabaseManager.World.CacheAllTreasureMaterialGroups();
                log.Info("Precaching Treasures - Material Colors...");
                DatabaseManager.World.CacheAllTreasureMaterialColor();
                log.Info("Precaching Treasures - Wielded...");
                DatabaseManager.World.CacheAllTreasureWielded();
            }
            else
                log.Info("Precaching World Database Disabled...");

            log.Info("Initializing RealmManager...");
            RealmManager.Initialize();

            log.Info("Initializing PlayerManager...");
            PlayerManager.Initialize();

            log.Info("Initializing HouseManager...");
            HouseManager.Initialize();

            log.Info("Initializing InboundMessageManager...");
            InboundMessageManager.Initialize();

            log.Info("Initializing SocketManager...");
            NetworkManager.Initialize(new NetworkManager());
            SocketManager.Initialize();

            log.Info("Initializing WorldManager...");
            WorldManager.Initialize();

            log.Info("Initializing EventManager...");
            EventManager.Initialize();

            // Free up memory before the server goes online. This can free up 6 GB+ on larger servers.
            log.Info("Forcing .net garbage collection...");
            for (int i = 0; i < 10; i++)
            {
                // https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/fundamentals
                // https://learn.microsoft.com/en-us/dotnet/api/system.runtime.gcsettings.largeobjectheapcompactionmode
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;

                GC.Collect();
            }

            // This should be last
            log.Info("Initializing CommandManager...");
            CommandManager.Initialize();

            //Register mod commands
            log.Info("Registering ModManager commands...");
            ModManager.RegisterCommands();
            ModManager.ListMods();

            if (!PropertyManager.GetBool("world_closed", false).Item)
            {
                WorldManager.Open(null);
            }
        }

        private static void CheckForServerUpdate()
        {
            log.Info($"Automatic Server version check not yet implemented for AC Realms.");
            return;

            //    log.Info($"Automatic Server version check started...");
            //    try
            //    {
            //        var worldDb = new Database.WorldDatabase();
            //        var currentVersion = worldDb.GetVersion();
            //        log.Info($"Current Server Binary: {ServerBuildInfo.FullVersion}");

            //        var url = "https://api.github.com/repos/ACEmulator/ACE/releases/latest";
            //        using var client = new WebClient();
            //        var html = client.GetStringFromURL(url).Result;

            //        var json = JsonSerializer.Deserialize<JsonElement>(html);

            //        string tag = json.GetProperty("tag_name").GetString();

            //        //Split the tag from "v{version}.{build}" into discrete components  - "tag_name": "v1.39.4192"
            //        Version v = new Version(tag.Remove(0, 1));
            //        Version currentServerVersion = ServerBuildInfo.GetServerVersion();

            //        var versionStatus = v.CompareTo(currentServerVersion);
            //        // Status returns > 0 if the GitHub version is newer. (0 if the same, or < 0 if older.)
            //        if (versionStatus > 0)
            //        {
            //            log.Warn("There is a newer version of AC Realms available!");
            //            log.Warn($"Please visit {json.GetProperty("html_url").GetString()} for more information.");

            //            // the Console.Title.Get() only works on Windows...
            //            #pragma warning disable CA1416 // Validate platform compatibility
            //            Console.Title += " -- Server Binary Update Available";
            //            #pragma warning restore CA1416 // Validate platform compatibility
            //        }
            //        else
            //        {
            //            log.Info($"Latest Server Version is {tag} -- No Update Required!");
            //        }
            //        return;
            //    }
            //    catch (Exception ex)
            //    {
            //        log.Info($"Unable to continue with Automatic Server Version Check due to the following error: {ex}");
            //    }
        }

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            log.Error(e.ExceptionObject);
        }

        private static void OnProcessExit(object sender, EventArgs e)
        {
            if (!Program.IsRunningInContainer)
            {
                if (!ServerManager.ShutdownInitiated)
                    log.Warn("Unsafe server shutdown detected! Data loss is possible!");

                PropertyManager.StopUpdating();
                DatabaseManager.Stop();

                // Do system specific cleanup here
                try
                {
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        MM_EndPeriod(1);
                    }
                }
                catch (Exception ex)
                {
                    log.Error(ex.ToString());
                }
            }
            else
            {
                ServerManager.DoShutdownNow();
                DatabaseManager.Stop();
            }
        }

    }
}
