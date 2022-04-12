using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Photon.Hive.Plugin;

namespace NaokaGo
{
    /// <summary>
    ///     Static configuration "global" for cleaner access.
    /// </summary>
    public class NaokaConfig
    {
        public IPluginLogger Logger;
        public IPluginHost Host;
        public Dictionary<string, string> Config;
        public Dictionary<string, string> ApiConfig;
        public Dictionary<string, object> RuntimeConfig; // Runtime configuration, including rate-limits, max actors per ip, etc.

        public readonly Dictionary<int, CustomActor> ActorsInternalProps = new Dictionary<int, CustomActor>();
    }

    public class NaokaGo : PluginBase
    {
        private readonly NaokaConfig naokaConfig = new NaokaConfig();
        private readonly EventLogic _EventLogic = new EventLogic();

        public override string Name => "Naoka";

        public override bool SetupInstance(IPluginHost host, Dictionary<string, string> config, out string errorMsg)
        {
            _EventLogic.Setup(naokaConfig);
            naokaConfig.Logger = host.CreateLogger(Name);
            naokaConfig.Host = host;
            naokaConfig.Config = config;
            naokaConfig.ApiConfig = new Dictionary<string, string>
            {
                {"ApiUrl", config["ApiUrl"]},
                {"PhotonSecret", config["PhotonApiSecret"]}
            };
            naokaConfig.RuntimeConfig = new Dictionary<string, object>()
            {
                {"configuredRateLimits", new Dictionary<byte, int>()},
                {"ratelimiterBoolean", false},
                {"maxAccsPerIp", 5},
            };

            var requestUri = $"{naokaConfig.ApiConfig["ApiUrl"]}/api/1/photon/getConfig?secret={naokaConfig.ApiConfig["PhotonSecret"]}";
            var apiResponse = new HttpClient().GetAsync(requestUri).Result.Content.ReadAsStringAsync().Result;
            var remoteConfig = JsonConvert.DeserializeObject<PhotonRuntimeRemoteConfig>(apiResponse);


            if (remoteConfig != null)
            {
                foreach (var kvp in remoteConfig.RateLimitList)
                {
                    ((Dictionary<byte, int>) naokaConfig.RuntimeConfig["configuredRateLimits"])[(byte) kvp.Key] =
                        kvp.Value;
                }

                naokaConfig.RuntimeConfig["ratelimiterBoolean"] = remoteConfig.RateLimitUnknownBool;
                naokaConfig.RuntimeConfig["maxAccsPerIp"] = remoteConfig.MaxAccsPerIp;
            }
            
            Task.Run(_EventLogic.RunEvent35Timer);

            return base.SetupInstance(host, config, out errorMsg);
        }

        public override void OnCreateGame(ICreateGameCallInfo info)
        {
            naokaConfig.RuntimeConfig["gameId"] = info.Request.GameId;
            
            PhotonValidateJoinJWTResponse jwtValidationResult =
                _EventLogic.ValidateJoinJwt((string) ((Hashtable) info.Request.Parameters[248])[(byte) 2]);
            bool tokenValid = jwtValidationResult.Valid;

            if (!tokenValid)
            {
                info.Fail("Invalid JWT presented.");
                return;
            }

            var user = new CustomActor
            {
                ActorNr = 1,
                Id = jwtValidationResult.User.Id,
                Ip = jwtValidationResult.Ip,
                JwtProperties = jwtValidationResult
            };
            naokaConfig.ActorsInternalProps.Add(1, user);
            
            _EventLogic.PrepareProperties(1, info.Request.ActorProperties, out var newProperties, out var error);
            if (error != "")
            {
                info.Fail(error);
            }

            info.Request.ActorProperties = newProperties;
            info.Continue();
            _EventLogic.SendModerationRequestToActor(1);
            _EventLogic.SendRatelimiterValues(1, (Dictionary<byte,int>)naokaConfig.RuntimeConfig["configuredRateLimits"], (bool)naokaConfig.RuntimeConfig["ratelimiterBoolean"]);
        }

        public override void OnJoin(IJoinGameCallInfo info)
        {
            
            PhotonValidateJoinJWTResponse jwtValidationResult = _EventLogic.ValidateJoinJwt((string)((Hashtable)info.Request.Parameters[248])[(byte)2]);
            bool tokenValid = jwtValidationResult.Valid;

            if (!tokenValid)
            {
                info.Fail("Invalid JWT presented.");
                return;
            }

            string userId = jwtValidationResult.User.Id;
            string ipAddress = jwtValidationResult.Ip;

            int ipAddressCount = 0;
            foreach (var actorInternalProp in naokaConfig.ActorsInternalProps)
            {
                if (actorInternalProp.Value.Id == userId && !jwtValidationResult.User.Tags.Contains("admin_moderator"))
                {
                    info.Fail("User is already in this room.");
                    return;
                }

                if (actorInternalProp.Value.Ip == ipAddress)
                    ++ipAddressCount;

                if (ipAddressCount > (int) naokaConfig.RuntimeConfig["maxAccsPerIp"])
                {
                    info.Fail("Max. account limit per instance reached. You've been bapped.");
                    return;
                }
            }
            
            var user = new CustomActor
            {
                ActorNr = info.Request.ActorNr,
                Id = userId,
                Ip = ipAddress,
                JwtProperties = jwtValidationResult
            };
            naokaConfig.ActorsInternalProps.Add(user.ActorNr, user);

            _EventLogic.PrepareProperties(info.ActorNr, info.Request.ActorProperties, out var newProperties, out var error);
            if (error != "")
            {
                info.Fail(error);
            }

            info.Request.ActorProperties = newProperties;
            info.Continue();
            
            _EventLogic.SendModerationRequestToActor(info.ActorNr);
            _EventLogic.SendModerationRequestToAllForActor(info.ActorNr);
            _EventLogic.SendRatelimiterValues(info.ActorNr, (Dictionary<byte,int>)naokaConfig.RuntimeConfig["configuredRateLimits"], (bool)naokaConfig.RuntimeConfig["ratelimiterBoolean"]);
        }

        public override void OnCloseGame(ICloseGameCallInfo info)
        {
            // TODO: Implement API call for instance removal. (e.g.: Reduce player count for instance in API).
            info.Continue();
        }

        public override void BeforeSetProperties(IBeforeSetPropertiesCallInfo info)
        {
            if (info.Request.ActorNumber == 0 || info.Request.ActorNumber != info.ActorNr)
            {
                naokaConfig.Host.RemoveActor(info.ActorNr, "*TeamSpeak Voice*: You have been kicked from the server.");
                info.Fail("*TeamSpeak Voice*: You have been kicked from the server.");
                return;
            }
            
            info.Request.Broadcast = true;

            _EventLogic.PrepareProperties(info.ActorNr, info.Request.Properties, out var temporaryPropertiesHt, out var propertiesError);
            if (propertiesError != "")
            {
                info.Fail(propertiesError);
                return;
            }
            
            info.Request.Properties = temporaryPropertiesHt;
            info.Request.Parameters[251] = temporaryPropertiesHt; // Better be safe than sorry.
            
            info.Continue();
        }

        public override void OnLeave(ILeaveGameCallInfo info)
        {
            naokaConfig.ActorsInternalProps.Remove(info.ActorNr);
            info.Continue();
        }
        
        public override void OnSetProperties(ISetPropertiesCallInfo info)
        {
            info.Continue();
        }

        public override void OnRaiseEvent(IRaiseEventCallInfo info)
        {
            switch (info.Request.EvCode)
            {
                case 0: // Unused event code. Trigger an alert if someone attempts to use this.
                    naokaConfig.Logger.Warn($"[Naoka]: EvCode 0 (Unused) called by {info.ActorNr} ({naokaConfig.ActorsInternalProps[info.ActorNr].Id}), This is very suspicious.");
                    info.Fail("Unauthorized.");
                    return;

                case 1: // uSpeak
                    info.Continue();
                    break;

                case 2: // ExecutiveMessage
                    naokaConfig.Logger.Warn($"[Naoka]: EvCode 2 (ExecutiveMessage) called by {info.ActorNr} ({naokaConfig.ActorsInternalProps[info.ActorNr].Id}), This is very suspicious.");
                    info.Fail("Unauthorized.");
                    return;

                case 3: // SendPastEvents; Only sent to MasterClient upon join.
                case 4: // SyncEvents
                case 5: // InitialSyncFinished
                case 6: // ProcessEvent
                case 7: // Serialization
                    info.Continue();
                    break;

                case 8: // ReceiveInterval (Interest List)
                    // var eventData8 = (byte[]) info.Request.Parameters[245];
                    // logEventEight(info.ActorNr, eventData8);

                    var parameters = new Dictionary<byte, object>();
                    int[] playerViews = naokaConfig.Host.GameActors.Select(actor => actor.ActorNr).ToArray();
                    parameters.Add(245, playerViews);
                    parameters.Add(254, 0);
                    SendParameters sendParams = default;
                    naokaConfig.Host.BroadcastEvent(
                        new List<int> {info.ActorNr},
                        0,
                        8,
                        parameters,
                        0, sendParams
                    );

                    info.Cancel();
                    break;

                case 9: // Udon, AV3Sync, BigData/ChairSync.
                case 15: // BigData/ChairSync? (Some sources say it's this, while others say it's 9).
                    info.Continue();
                    break;

                case 33: // ExecutiveAction
                    Dictionary<byte,object> eventData = (Dictionary<byte,object>)info.Request.Parameters[245];

                    switch((byte) eventData[ExecutiveActionPacket.Type])
                    {
                        case ExecutiveActionTypes.Request_PlayerMods:
                        {
                            // This is a reply to the server's request.
                            // It contains a String[][]:
                            //   - []string: blockedUsers
                            //   - []string: mutedUsers
                            // The strings are the first six characters of the user's ID, exclusive of `usr_`

                            var blockedUsers = ((string[][]) eventData[3])[0];
                            var mutedUsers = ((string[][]) eventData[3])[1];

                            foreach (var actor in naokaConfig.ActorsInternalProps)
                            {
                                if (actor.Value.ActorNr != info.ActorNr)
                                {
                                    var u = (actor.Value.Id).Substring(4, 6);
                                    var isBlocked = blockedUsers.Contains(u);
                                    var isMuted = mutedUsers.Contains(u);

                                    _EventLogic.SendModerationReply(info.ActorNr, actor.Key, isBlocked,
                                        isMuted);
                                }
                            }

                            break;
                        }
                        case ExecutiveActionTypes.Kick:
                        {
                            // TODO: Only process if the user is the instance creator, or world author.
                            //       Current implementation will allow only the current master of the instance to kick.

                            if (naokaConfig.Host.MasterClientId != info.ActorNr)
                            {
                                info.Fail("Only the master client can kick other users.");
                                break;
                            }
                            
                            var target = naokaConfig.ActorsInternalProps.FirstOrDefault(actor => actor.Value.Id == eventData[ExecutiveActionPacket.Target_User].ToString());
                            if (target.Key == 0)
                            {
                                naokaConfig.Logger.Info($"Could not find target user ({eventData[ExecutiveActionPacket.Target_User]}) for ExecutiveAction Kick sent by {info.ActorNr} ({naokaConfig.ActorsInternalProps[info.ActorNr].Id})");
                                break;
                            }
                            
                            naokaConfig.Logger.Info($"Kicking {target.Key} ({target.Value.Id}) for ExecutiveAction Kick sent by {info.ActorNr} ({naokaConfig.ActorsInternalProps[info.ActorNr].Id})");
                            _EventLogic.SendExecutiveMessage(target.Key, (string)eventData[ExecutiveActionPacket.Main_Property]);
                            
                            break;
                        }
                        case ExecutiveActionTypes.Warn:
                        {
                            break;
                        }
                    }

                    info.Cancel();
                    break;

                case 40: // UserRecordUpdate
                    _EventLogic.PullPartialActorProperties(info.ActorNr, naokaConfig.ActorsInternalProps[info.ActorNr].Id);

                    info.Cancel();
                    break;

                case 42: // Updating AvatarEyeHeight property
                    // TODO: Filter out anything that is irrelevant to this event. At the moment, it acts as if its a SetProperties.
                    //       I uh, have no clue what *could* be relevant, what I *do* know is, is `avatarEyeHeight`.
                    _EventLogic.PrepareProperties(info.ActorNr, (Hashtable)info.Request.Data, out var temporaryPropertiesHt, out var propertiesError);
                    if (propertiesError != "")
                    {
                        info.Fail(propertiesError);
                        return;
                    }
                    
                    // TODO: Disable older property setting. This behavior has been moved to 42.
                    naokaConfig.Host.SetProperties(info.ActorNr, temporaryPropertiesHt, null, true);
                    _EventLogic.SendProperties(temporaryPropertiesHt, info.ActorNr);
                    info.Cancel();
                    break;
                default: // Unknown event code; Log and cancel.
                    if (info.Request.EvCode < 200)
                    {
                        naokaConfig.Logger.Warn($"[Naoka]: Unknown Event code `{info.Request.EvCode}`.");
                        naokaConfig.Logger.Warn($"{JsonConvert.SerializeObject(info.Request.Data, Formatting.Indented)}");
                        info.Cancel();
                    }
                    break;
            }

            if (info.Request.EvCode >= 200)
            {
                switch (info.Request.EvCode)
                {
                    case 202:
                        if ((string)((Hashtable)info.Request.Parameters[245])[(byte)0] != "VRCPlayer")
                        {
                            info.Fail("Only VRCPlayer can be spawned.");
                            return;
                        }
                        if ((string)((Hashtable)info.Request.Parameters[245])[(byte)0] == "VRCPlayer" && naokaConfig.ActorsInternalProps[info.ActorNr].Instantiated) {
                            info.Fail("Already Instantiated");
                            return;
                        }

                        if ((string) ((Hashtable) info.Request.Parameters[245])[(byte)0] == "VRCPlayer")
                            naokaConfig.ActorsInternalProps[info.ActorNr].Instantiated = true;
                        info.Request.Cache = CacheOperations.AddToRoomCache;
                        
                        // Force instantiation at 0,0,0 by removing the Vec3 and Quaternion types from the request.
                        ((Hashtable)info.Request.Parameters[245]).Remove((byte)1);
                        ((Hashtable)info.Request.Parameters[245]).Remove((byte)2);

                        break;
                }
                info.Continue();
            }
        }

        private void LogEventEight(int actorNr, byte[] data)
        {
            var posInEvent8 = 0;

            var actorInterests = new Dictionary<Int32, Int16>();
            while (posInEvent8 < data.Length)
            {
                var viewid = BitConverter.ToInt32(data, posInEvent8);
                posInEvent8 += 4;
                var interest = BitConverter.ToInt16(data, posInEvent8);
                actorInterests.Add(viewid, interest);
                posInEvent8 += 2;
            }

            foreach (var kvp in actorInterests)
            {
                naokaConfig.Logger.Info($"[Naoka]:    {actorNr} set interest of ViewID {kvp.Key} to {kvp.Value}");
            }
        }
    }

    public class NaokaGoFactory : PluginFactoryBase
    {
        public override IGamePlugin CreatePlugin(string pluginName)
        {
            return new NaokaGo();
        }
    }
}
