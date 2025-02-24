using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;

using log4net;

using ACE.Common;
using ACE.Common.Performance;
using ACE.Database;
using ACE.Database.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.WorldObjects;
using ACE.Server.Network;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Network.Managers;
using ACE.Server.Physics;
using ACE.Server.Physics.Common;

using Character = ACE.Database.Models.Shard.Character;
using Position = ACE.Entity.Position;
using ACE.Server.Realms;
using ACE.Entity.Enum.RealmProperties;
using ACE.Server.Network.Enum;
using System.Linq;
using ACRealms.Server.Network.TraceMessages.Messages;

namespace ACE.Server.Managers
{
    public static class WorldManager
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private static readonly PhysicsEngine Physics;

        public static bool WorldActive { get; private set; }
        private static volatile bool pendingWorldStop;

        public enum WorldStatusState
        {
            Closed,
            Open
        }

        public static WorldStatusState WorldStatus { get; private set; } = WorldStatusState.Closed;

        public static readonly ActionQueue ActionQueue = new ActionQueue();
        public static readonly DelayManager DelayManager = new DelayManager();

        static WorldManager()
        {
            Physics = new PhysicsEngine(new ObjectMaint(), new SmartBox());
            Physics.Server = true;
        }

        public static void Initialize()
        {
            var thread = new Thread(() =>
            {
                LandblockManager.PreloadConfigLandblocks();
                UpdateWorld();
            });
            thread.Name = "World Manager";
            thread.Priority = ThreadPriority.AboveNormal;
            thread.Start();
            log.DebugFormat("ServerTime initialized to {0}", Timers.WorldStartLoreTime);
            log.DebugFormat("Current maximum allowed sessions: {0}", ConfigManager.Config.Server.Network.MaximumAllowedSessions);

            log.Info($"World started and is currently {WorldStatus.ToString()}{(PropertyManager.GetBool("world_closed", false).Item ? "" : " and will open automatically when server startup is complete.")}");
            if (WorldStatus == WorldStatusState.Closed)
                log.Info($"To open world to players, use command: world open");
        }

        internal static void Open(Player player)
        {
            WorldStatus = WorldStatusState.Open;
            PlayerManager.BroadcastToAuditChannel(player, "World is now open");
        }

        internal static void Close(Player player, bool bootPlayers = false)
        {
            WorldStatus = WorldStatusState.Closed;
            var msg = "World is now closed";
            if (bootPlayers)
                msg += ", and booting all online players.";

            PlayerManager.BroadcastToAuditChannel(player, msg);

            if (bootPlayers)
                PlayerManager.BootAllPlayers();
        }

        public static void PlayerInitForWorld(ISession session, uint guid, string clientString)
        {
            if (ServerManager.ShutdownInProgress)
            {
                session.SendCharacterError(CharacterError.LogonServerFull);
                return;
            }

            if (clientString != session.Account)
            {
                session.SendCharacterError(CharacterError.EnterGameCharacterNotOwned);
                return;
            }

            var character = session.Characters.SingleOrDefault(c => c.Id == guid);
            if (character == null)
            {
                session.SendCharacterError(CharacterError.EnterGameCharacterNotOwned);
                return;
            }

            if (character.IsDeleted || character.DeleteTime > 0)
            {
                session.SendCharacterError(CharacterError.EnterGameCharacterNotOwned);
                return;
            }

            if (PlayerManager.GetOnlinePlayer(guid) != null)
            {
                // If this happens, it could be that the previous session for this Player terminated in a way that didn't transfer the player to offline via PlayerManager properly.
                session.SendCharacterError(CharacterError.EnterGameCharacterInWorld);
                return;
            }

            var offlinePlayer = PlayerManager.GetOfflinePlayer(guid);

            if (offlinePlayer == null)
            {
                // This would likely only happen if the account tried to log in a character that didn't exist.
                session.SendCharacterError(CharacterError.EnterGameGeneric);
                return;
            }

            if (offlinePlayer.IsDeleted || offlinePlayer.IsPendingDeletion)
            {
                session.SendCharacterError(CharacterError.EnterGameCharacterNotOwned);
                return;
            }

            if ((offlinePlayer.Heritage == (int)HeritageGroup.Olthoi || offlinePlayer.Heritage == (int)HeritageGroup.OlthoiAcid) && PropertyManager.GetBool("olthoi_play_disabled").Item)
            {
                session.SendCharacterError(CharacterError.EnterGameCouldntPlaceCharacter);
                return;
            }

            session.InitSessionForWorldLogin();

            session.State = SessionState.WorldConnected;

            PlayerEnterWorld(session, character);
        }

        public static void PlayerEnterWorld(ISession session, Character character)
        {
            var offlinePlayer = PlayerManager.GetOfflinePlayer(character.Id);

            if (offlinePlayer == null)
            {
                log.Error($"PlayerEnterWorld requested for character.Id 0x{character.Id:X8} not found in PlayerManager OfflinePlayers.");
                return;
            }

            var start = DateTime.UtcNow;
            DatabaseManager.Shard.GetPossessedBiotasInParallel(character.Id, biotas =>
            {
                log.DebugFormat("GetPossessedBiotasInParallel for {0} took {1:N0} ms", character.Name, (DateTime.UtcNow - start).TotalMilliseconds);

                ActionQueue.EnqueueAction(new ActionEventDelegate(() => DoPlayerEnterWorld(session, character, offlinePlayer.Biota, biotas)));
            });
        }

        private static void DoPlayerEnterWorld(ISession session, Character character, Biota playerBiota, PossessedBiotas possessedBiotas)
        {
            Player player;

            Player.HandleNoLogLandblock(playerBiota, out var playerLoggedInOnNoLogLandblock);

            var stripAdminProperties = false;
            var addAdminProperties = false;
            var addSentinelProperties = false;
            if (ConfigManager.Config.Server.Accounts.OverrideCharacterPermissions)
            {
                if (session.AccessLevel <= AccessLevel.Advocate) // check for elevated characters
                {
                    if (playerBiota.WeenieType == WeenieType.Admin || playerBiota.WeenieType == WeenieType.Sentinel) // Downgrade weenie
                    {
                        character.IsPlussed = false;
                        playerBiota.WeenieType = WeenieType.Creature;
                        stripAdminProperties = true;
                    }
                }
                else if (session.AccessLevel >= AccessLevel.Sentinel && session.AccessLevel <= AccessLevel.Envoy)
                {
                    if (playerBiota.WeenieType == WeenieType.Creature || playerBiota.WeenieType == WeenieType.Admin) // Up/downgrade weenie
                    {
                        character.IsPlussed = true;
                        playerBiota.WeenieType = WeenieType.Sentinel;
                        addSentinelProperties = true;
                    }
                }
                else // Developers and Admins
                {
                    if (playerBiota.WeenieType == WeenieType.Creature || playerBiota.WeenieType == WeenieType.Sentinel) // Up/downgrade weenie
                    {
                        character.IsPlussed = true;
                        playerBiota.WeenieType = WeenieType.Admin;
                        addAdminProperties = true;
                    }
                }
            }

            if (playerBiota.WeenieType == WeenieType.Admin)
                player = new Admin(playerBiota, possessedBiotas.Inventory, possessedBiotas.WieldedItems, character, session);
            else if (playerBiota.WeenieType == WeenieType.Sentinel)
                player = new Sentinel(playerBiota, possessedBiotas.Inventory, possessedBiotas.WieldedItems, character, session);
            else
                player = new Player(playerBiota, possessedBiotas.Inventory, possessedBiotas.WieldedItems, character, session);

            session.SetPlayer(player);

            if (stripAdminProperties) // continue stripping properties
            {
                player.CloakStatus = CloakStatus.Undef;
                player.Attackable = true;
                player.SetProperty(PropertyBool.DamagedByCollisions, true);
                player.AdvocateLevel = null;
                player.ChannelsActive = null;
                player.ChannelsAllowed = null;
                player.Invincible = false;
                player.Cloaked = null;
                player.IgnoreHouseBarriers = false;
                player.IgnorePortalRestrictions = false;
                player.SafeSpellComponents = false;
                player.ReportCollisions = true;


                player.ChangesDetected = true;
                player.CharacterChangesDetected = true;
            }

            if (addSentinelProperties || addAdminProperties) // continue restoring properties to default
            {
                WorldObject weenie;

                if (addAdminProperties)
                    weenie = Factories.WorldObjectFactory.CreateWorldObject(DatabaseManager.World.GetCachedWeenie("admin"), new ACE.Entity.ObjectGuid(ACE.Entity.ObjectGuid.Invalid.Full));
                else
                    weenie = Factories.WorldObjectFactory.CreateWorldObject(DatabaseManager.World.GetCachedWeenie("sentinel"), new ACE.Entity.ObjectGuid(ACE.Entity.ObjectGuid.Invalid.Full));

                if (weenie != null)
                {
                    player.CloakStatus = CloakStatus.Off;
                    player.Attackable = weenie.Attackable;
                    player.SetProperty(PropertyBool.DamagedByCollisions, false);
                    player.AdvocateLevel = weenie.GetProperty(PropertyInt.AdvocateLevel);
                    player.ChannelsActive = (Channel?)weenie.GetProperty(PropertyInt.ChannelsActive);
                    player.ChannelsAllowed = (Channel?)weenie.GetProperty(PropertyInt.ChannelsAllowed);
                    player.Invincible = false;
                    player.Cloaked = false;


                    player.ChangesDetected = true;
                    player.CharacterChangesDetected = true;
                }
            }

            // If the client is missing a location, we start them off in the starter town they chose
            if (session.Player.Location == null)
            {
                if (session.Player.Instantiation != null)
                    session.Player.Location = session.Player.Instantiation;
                else
                    session.Player.Location = RealmManager.GetRealm(session.Player.HomeRealm).DefaultStartingLocation(session.Player);  // realm fallback
            }

            //var realm = RealmManager.GetRealm(session.Player.Location.RealmID);
            //if (realm == null)
            //{
            //    var homerealm = RealmManager.GetRealm(session.Player.HomeRealm);
            //    if (homerealm == null)
            //        homerealm = RealmManager.GetRealm((ushort)ReservedRealm.@default);
            //    var pos = new Position(session.Player.Location);
            //    pos.SetToDefaultRealmInstance(homerealm.Realm.Id);

            //    log.Error($"WorldManager.DoPlayerEnterWorld: failed to find realm {session.Player.Location.RealmID}, for player {session.Player.Name}, relocating to realm {homerealm.Realm.Id}.");
            //    session.Player.Location = pos;
            //}
            if (!session.Player.ValidatePlayerRealmPosition(session.Player.Location))
            {
                var homerealm = RealmManager.GetRealm(session.Player.HomeRealm);
                if (homerealm == null)
                    homerealm = RealmManager.GetRealm((ushort)ReservedRealm.@default);
                if (session.Player.GetPosition(PositionType.EphemeralRealmExitTo) != null)
                {
                    session.Network.EnqueueSend(new GameMessageSystemChat($"The instance you were in has expired and you have been transported outside!", ChatMessageType.System));
                    session.Player.ExitInstance();
                }
                else
                {
                    var pos = session.Player.Location.AsLocalPosition().AsInstancedPosition(session.Player, PlayerInstanceSelectMode.HomeRealm);
                    session.Network.EnqueueSend(new GameMessageSystemChat($"You have been transported back to your home realm.", ChatMessageType.System));
                    log.Info($"WorldManager.DoPlayerEnterWorld: player {session.Player.Name} doesn't have permission to be in realm {session.Player.Location.RealmID}, relocating to realm {homerealm.Realm.Id}.");
                    session.Player.Location = pos;
                }
            }

            var olthoiPlayerReturnedToLifestone = session.Player.IsOlthoiPlayer && character.TotalLogins >= 1 && session.Player.LoginAtLifestone;
            if (olthoiPlayerReturnedToLifestone)
                session.Player.Location = session.Player.Sanctuary.AsInstancedPosition(session.Player, PlayerInstanceSelectMode.HomeRealm);

            session.Player.PlayerEnterWorld();

            var success = LandblockManager.AddObject(session.Player, true);
            if (!success)
            {
                // send to lifestone, or fallback location
                var fixLoc = (session.Player.Sanctuary ?? RealmManager.GetRealm(session.Player.HomeRealm).DefaultStartingLocation(session.Player).AsLocalPosition())
                    .AsInstancedPosition(session.Player, PlayerInstanceSelectMode.HomeRealm);

                log.Error($"WorldManager.DoPlayerEnterWorld: failed to spawn {session.Player.Name}, relocating to {fixLoc.ToLOCString()}");

                session.Player.Location = fixLoc;
                LandblockManager.AddObject(session.Player, true);

                var actionChain = new ActionChain();
                actionChain.AddDelaySeconds(5.0f);
                actionChain.AddAction(session.Player, () =>
                {
                    if (session != null && session.Player != null)
                        session.Player.Teleport(fixLoc);
                });
                actionChain.EnqueueChain();
            }

            // These warnings are set by DDD_InterrogationResponse
            if ((session.DatWarnCell || session.DatWarnLanguage || session.DatWarnPortal) && PropertyManager.GetBool("show_dat_warning").Item)
            {
                var msg = PropertyManager.GetString("dat_older_warning_msg").Item;
                var chatMsg = new GameMessageSystemChat(msg, ChatMessageType.System);
                session.Network.EnqueueSend(chatMsg);
            }

            var popup_header = PropertyManager.GetString("popup_header").Item;
            var popup_motd = PropertyManager.GetString("popup_motd").Item;
            var popup_welcome = player.IsOlthoiPlayer ? PropertyManager.GetString("popup_welcome_olthoi").Item : PropertyManager.GetString("popup_welcome").Item;

            if (character.TotalLogins <= 1)
            {
                if (player.IsOlthoiPlayer)
                    session.Network.EnqueueSend(new GameEventPopupString(session, AppendLines(popup_welcome, popup_motd)));
                else
                    session.Network.EnqueueSend(new GameEventPopupString(session, AppendLines(popup_header, popup_motd, popup_welcome)));
            }
            else if (!string.IsNullOrEmpty(popup_motd))
            {
                session.Network.EnqueueSend(new GameEventPopupString(session, AppendLines(popup_header, popup_motd)));
            }

            var info = "Welcome to Asheron's Call\n  powered by AC Realms\n\nFor more information on commands supported by this server, type @acehelp\n";
            session.Network.EnqueueSend(new GameMessageSystemChat(info, ChatMessageType.Broadcast));

            var server_motd = PropertyManager.GetString("server_motd").Item;
            if (!string.IsNullOrEmpty(server_motd))
                session.Network.EnqueueSend(new GameMessageSystemChat($"{server_motd}\n", ChatMessageType.Broadcast));

            if (olthoiPlayerReturnedToLifestone)
                session.Network.EnqueueSend(new GameMessageSystemChat("You have returned to the Olthoi Queen to serve the hive.", ChatMessageType.Broadcast));
            else if (playerLoggedInOnNoLogLandblock) // see http://acpedia.org/wiki/Mount_Elyrii_Hive
                session.Network.EnqueueSend(new GameMessageSystemChat("The currents of portal space cannot return you from whence you came. Your previous location forbids login.", ChatMessageType.Broadcast));

            session.Network.EnqueueSend(new TraceMessageEnterWorldComplete());
        }

        private static string AppendLines(params string[] lines)
        {
            var result = "";
            foreach (var line in lines)
                if (!string.IsNullOrEmpty(line))
                    result += $"{line}\n";

            return Regex.Replace(result, "\n$", "");
        }

        /// <summary>
        /// ACE allows for multi-threading with thread boundaries based on the "LandblockGroup" concept
        /// The risk of moving the player immediately is that the player may move onto another LandblockGroup, and thus, cross thread boundaries
        /// This will enqueue the work onto WorldManager making the teleport thread safe.
        /// Note that this work will be done on the next tick, not immediately, so be careful about your order of operations.
        /// If you must ensure order, pass your follow up work in with the argument actionToFollowUpWith. That work will be enqueued onto the Player.
        /// </summary>
        public static void ThreadSafeTeleport(Player player, InstancedPosition newPosition, bool teleportingFromInstance, IAction actionToFollowUpWith = null, bool fromPortal = false)
        {
            EnqueueAction(new ActionEventDelegate(() =>
            {
                player.Teleport(newPosition, teleportingFromInstance, fromPortal);

                if (actionToFollowUpWith != null)
                    EnqueueAction(actionToFollowUpWith);
            }));
        }

        public static void EnqueueAction(IAction action)
        {
            ActionQueue.EnqueueAction(action);
        }

        private static readonly RateLimiter updateGameWorldRateLimiter = new RateLimiter(60, TimeSpan.FromSeconds(1));

        /// <summary>
        /// Manages updating all entities on the world.
        ///  - Server-side command-line commands are handled in their own thread.
        ///  - Database I/O is handled in its own thread.
        ///  - Network commands come from their own listener threads, and are queued for each sessions which are then processed here.
        ///  - This thread does the rest of the work!
        /// </summary>
        private static void UpdateWorld()
        {
            log.DebugFormat("Starting UpdateWorld thread");

            WorldActive = true;
            var worldTickTimer = new Stopwatch();

            while (!pendingWorldStop)
            {
                /*
                When it comes to thread safety for Landblocks and WorldObjects, ACE makes the following assumptions:

                 * Inbound ClientMessages and GameActions are handled on the main UpdateWorld thread.
                   - These actions may load Landblocks and modify other WorldObjects safely.

                 * PlayerEnterWorld queue is run on the main UpdateWorld thread.
                   - These actions may load Landblocks and modify other WorldObjects safely.

                 * Landblock Groups (calculated by LandblockManager) can be processed in parallel.

                 * Adjacent Landblocks will always be run on the same thread.

                 * Non-adjacent landblocks might be run on different threads.
                   - If two non-adjacent landblocks both touch the same landblock, and that landblock is active, they will be run on the same thread.

                 * Database results are returned from a task spawned in SerializedShardDatabase (via callback).
                   - Minimal processing should be done from the callback. Return as quickly as possible to let the database thread do database work.
                   - The processing of these results should be queued to an ActionQueue

                 * The only cases where it's acceptable for to create a new Task, Thread or Parallel loop are the following:
                   - Every scenario must be one where you don't care about breaking ACE
                   - DeveloperCommand Handlers
                */

                worldTickTimer.Restart();

                ServerPerformanceMonitor.RestartEvent(ServerPerformanceMonitor.MonitorType.PlayerManager_Tick);
                PlayerManager.Tick();
                ServerPerformanceMonitor.RegisterEventEnd(ServerPerformanceMonitor.MonitorType.PlayerManager_Tick);

                ServerPerformanceMonitor.RestartEvent(ServerPerformanceMonitor.MonitorType.NetworkManager_InboundClientMessageQueueRun);
                NetworkManager.Instance.InboundMessageQueue.RunActions();
                ServerPerformanceMonitor.RegisterEventEnd(ServerPerformanceMonitor.MonitorType.NetworkManager_InboundClientMessageQueueRun);

                // This will consist of PlayerEnterWorld actions, as well as other game world actions that require thread safety
                ServerPerformanceMonitor.RestartEvent(ServerPerformanceMonitor.MonitorType.actionQueue_RunActions);
                ActionQueue.RunActions();
                ServerPerformanceMonitor.RegisterEventEnd(ServerPerformanceMonitor.MonitorType.actionQueue_RunActions);

                ServerPerformanceMonitor.RestartEvent(ServerPerformanceMonitor.MonitorType.DelayManager_RunActions);
                DelayManager.RunActions();
                ServerPerformanceMonitor.RegisterEventEnd(ServerPerformanceMonitor.MonitorType.DelayManager_RunActions);

                ServerPerformanceMonitor.RestartEvent(ServerPerformanceMonitor.MonitorType.UpdateGameWorld);
                var gameWorldUpdated = UpdateGameWorld();
                ServerPerformanceMonitor.RegisterEventEnd(ServerPerformanceMonitor.MonitorType.UpdateGameWorld);

                ServerPerformanceMonitor.RestartEvent(ServerPerformanceMonitor.MonitorType.NetworkManager_DoSessionWork);
                int sessionCount = NetworkManager.Instance.DoSessionWork();
                ServerPerformanceMonitor.RegisterEventEnd(ServerPerformanceMonitor.MonitorType.NetworkManager_DoSessionWork);

                ServerPerformanceMonitor.Tick();

                // We only relax the CPU if our game world is able to update at the target rate.
                // We do not sleep if our game world just updated. This is to prevent the scenario where our game world can't keep up. We don't want to add further delays.
                // If our game world is able to keep up, it will not be updated on most ticks. It's on those ticks (between updates) that we will relax the CPU.
                if (!gameWorldUpdated)
                    Thread.Sleep(sessionCount == 0 ? 10 : 1); // Relax the CPU more if no sessions are connected

                Timers.PortalYearTicks += worldTickTimer.Elapsed.TotalSeconds;
            }

            // World has finished operations and concedes the thread to garbage collection
            WorldActive = false;
        }

        /// <summary>
        /// Projected to run at a reasonable rate for gameplay (30-60fps)
        /// </summary>
        public static bool UpdateGameWorld()
        {
            if (updateGameWorldRateLimiter.GetSecondsToWaitBeforeNextEvent() > 0)
                return false;

            updateGameWorldRateLimiter.RegisterEvent();

            ServerPerformanceMonitor.RestartCumulativeEvents();
            ServerPerformanceMonitor.RestartEvent(ServerPerformanceMonitor.MonitorType.UpdateGameWorld_Entire);

            LandblockManager.Tick(Timers.PortalYearTicks);

            HouseManager.Tick();

            ServerPerformanceMonitor.RegisterEventEnd(ServerPerformanceMonitor.MonitorType.UpdateGameWorld_Entire);
            ServerPerformanceMonitor.RegisterCumulativeEvents();

            return true;
        }

        /// <summary>
        /// Function to begin ending the operations inside of an active world.
        /// </summary>
        public static void StopWorld() { pendingWorldStop = true; }
    }
}
