














using System;
using System.Collections.Generic;
using System.Linq;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("TwinxHUD", "Twinx", "2.5.0")]
    [Description("Twinx Rust HUD built from separate PNG assets")]
    public class TwinxHUD : RustPlugin
    {
        [PluginReference] private Plugin ImageLibrary, PlayerRanks, PlaytimeTracker;

        private const string Root = "TwinxHUD.Root";
        private const string Launcher = "TwinxHUD.Launcher";
        private const string PermUse = "twinxhud.use";
        private readonly HashSet<ulong> opened = new HashSet<ulong>();
        private readonly HashSet<ulong> countedPlants = new HashSet<ulong>();
        private readonly Dictionary<ulong, string> currentTabs = new Dictionary<ulong, string>();
        private readonly Dictionary<ulong, string> currentTopCategories = new Dictionary<ulong, string>();
        private StatsData statsData = new StatsData();
        private ProgressData progressData = new ProgressData();

        // Read-only mirror of TwinxCoin_Data. Used because custom Plugin.Call hooks
        // are not exposed consistently by this server's Oxide build.
        private class CoinDataMirror
        {
            public Dictionary<ulong, CoinWalletMirror> Wallets = new Dictionary<ulong, CoinWalletMirror>();
            public Dictionary<string, bool> RewardClaims = new Dictionary<string, bool>();
        }

        private class CoinWalletMirror
        {
            public long Balance;
        }

        private class StatsData
        {
            public Dictionary<ulong, int> AnimalKills = new Dictionary<ulong, int>();
            public Dictionary<ulong, int> PlantsPlanted = new Dictionary<ulong, int>();
        }

        private class ProgressData
        {
            public Dictionary<ulong, ProgressRecord> Players = new Dictionary<ulong, ProgressRecord>();
        }

        private class ProgressRecord
        {
            public double TotalXp;
            public int XpVersion;
            // Highest level whose Coin reward has been confirmed by TwinxCoin.
            public int LastRewardedLevel;
            public StatsSnapshot Last = new StatsSnapshot();
        }

        private class StatsSnapshot
        {
            public double Kills, NpcKills, AnimalKills, HeliKills, APCKills, Shots, Built, Upgraded, Demolished;
            public double Crafted, Deployed, PlantsPlanted, PlantsGathered, Playtime;
        }

        private const string ImageBase = "https://raw.githubusercontent.com/mezisterbiz-art/TwinxHUD-Images/main/";
        private readonly Dictionary<string, string> imageFiles = new Dictionary<string, string>
        {
            ["main"] = "main_background.png",
            ["sidebar"] = "sidebar_background.png",
            ["profile"] = "profile_banner.png",
            ["bottom"] = "bottom_banner.png",
            ["pvp"] = "panel_pvp.png",
            ["pve"] = "panel_pve.png",
            ["activity"] = "panel_activity.png",
            ["card"] = "stat_card.png",
            ["xpbg"] = "xp_bar_bg.png",
            ["xpfill"] = "xp_bar_fill.png",
            ["active"] = "button_active.png",
            ["normal"] = "button_normal.png",
            ["close"] = "close_button.png",
            ["open"] = "hud_open_button.png",
            ["coin_panel"] = "Twinx_Coin_Panel.png",
            ["top_icon_pvp"] = "top_icon_pvp.png",
            ["top_icon_pve"] = "top_icon_pve.png",
            ["top_icon_resources"] = "top_icon_resources.png",
            ["top_icon_build"] = "top_icon_build.png",
            ["top_icon_time"] = "top_icon_time.png",
            ["top_crate_gold"] = "top_crate_gold.png",
            ["top_crate_purple"] = "top_crate_purple.png",
            ["top_crate_green"] = "top_crate_green.png",
            ["top_medal_gold"] = "top_medal_gold.png",
            ["top_medal_silver"] = "top_medal_silver.png",
            ["top_medal_bronze"] = "top_medal_bronze.png",

            // Иконки левого меню
            ["icon_rewards"] = "_0005_icon_rewards.png",
            ["icon_events"] = "_0006_icon_events.png",
            ["icon_top"] = "_0007_icon_top.png",
            ["icon_stats"] = "_0008_icon_stats.png",
            ["icon_cases"] = "_0009_icon_cases.png",
            ["icon_achievements"] = "_0010_icon_achievements.png",
            ["icon_shop"] = "_0011_icon_shop.png",

            // Иконки карточек статистики
            ["stat_kills"] = "_0000_stat_kills.png",
            ["stat_playtime"] = "_0001_stat_playtime.png",
            ["stat_deaths"] = "_0002_stat_deaths.png",
            ["stat_kd"] = "_0003_stat_kd.png",
            ["stat_npc"] = "_0004_stat_npc.png"
        };

        private void Init()
        {
            permission.RegisterPermission(PermUse, this);
            cmd.AddChatCommand("stats", this, nameof(OpenStatsCommand));
            cmd.AddChatCommand("top", this, nameof(OpenTopCommand));
            cmd.AddChatCommand("teamstats", this, nameof(OpenTeamStatsCommand));
            cmd.AddChatCommand("events", this, nameof(OpenEventsCommand));
        }

        private void OnServerInitialized()
        {
            LoadStatsData();
            LoadProgressData();
            LoadImages();
            foreach (BasePlayer p in BasePlayer.activePlayerList)
            {
                timer.Once(2f, () => { if (p != null && p.IsConnected) { SyncProgress(p); DrawLauncher(p); } });
                timer.Once(3f, () => { if (p != null && p.IsConnected && !opened.Contains(p.userID)) DrawLauncher(p); });
            }
            timer.Every(5f, SyncAllProgress);
            timer.Every(60f, RefreshTopPages);
        }

        private void OnPluginLoaded(Plugin plugin)
        {
            if (plugin == null) return;
            if (plugin.Name == "ImageLibrary") timer.Once(1f, LoadImages);
            if (plugin.Name == "TwinxCoin")
            {
                timer.Once(1f, () =>
                {
                    foreach (BasePlayer player in BasePlayer.activePlayerList)
                        if (player != null && player.IsConnected) RefreshCoinDisplay(player);
                });
            }
        }

        private void LoadImages()
        {
            if (ImageLibrary == null)
            {
                PrintWarning("ImageLibrary is not loaded; TwinxHUD images were not queued.");
                return;
            }

            int remaining = imageFiles.Count;
            foreach (var pair in imageFiles)
            {
                try
                {
                    // ImageLibrary 2.0.69 API requires imageId as the third argument.
                    // Its downloads are asynchronous, so redraw open Top pages after each completed load.
                    Action callback = () =>
                    {
                        remaining--;
                        if (remaining <= 0)
                            timer.Once(1f, RefreshTopPages);
                    };
                    ImageLibrary.Call("AddImage", ImageBase + pair.Value, "twinx_" + pair.Key, 0UL, callback);
                }
                catch (Exception ex)
                {
                    remaining--;
                    PrintWarning("ImageLibrary: " + pair.Key + " - " + ex.Message);
                    if (remaining <= 0)
                        timer.Once(1f, RefreshTopPages);
                }
            }

            // Fallback refresh in case a callback is not returned by the installed ImageLibrary build.
            timer.Once(30f, RefreshTopPages);
        }

        private void OnPlayerInit(BasePlayer player)
        {
            // OnPlayerInit can run before the client has finished connecting.
            // Keep a short fallback here; the primary draw is handled by OnPlayerConnected.
            timer.Once(2f, () =>
            {
                if (player != null && player.IsConnected && !opened.Contains(player.userID))
                {
                    SyncProgress(player);
                    DrawLauncher(player);
                }
            });
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            if (player == null) return;

            // Draw after the connection is established, then retry once in case
            // the client's HUD layer was not ready on the first attempt.
            timer.Once(1f, () =>
            {
                if (player != null && player.IsConnected && !opened.Contains(player.userID))
                {
                    SyncProgress(player);
                    DrawLauncher(player);
                }
            });

            timer.Once(3f, () =>
            {
                if (player != null && player.IsConnected && !opened.Contains(player.userID))
                    DrawLauncher(player);
            });
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            SyncProgress(player);
            DestroyAll(player);
            opened.Remove(player.userID);
            currentTabs.Remove(player.userID);
            SaveProgressData();
            currentTopCategories.Remove(player.userID);
        }

        private void Unload()
        {
            SaveStatsData();
            foreach (BasePlayer p in BasePlayer.activePlayerList) SyncProgress(p);
            SaveProgressData();
            foreach (BasePlayer p in BasePlayer.activePlayerList) DestroyAll(p);
        }

        private void LoadStatsData()
        {
            try
            {
                statsData = Interface.Oxide.DataFileSystem.ReadObject<StatsData>("TwinxHUD_Stats") ?? new StatsData();
                if (statsData.AnimalKills == null) statsData.AnimalKills = new Dictionary<ulong, int>();
                if (statsData.PlantsPlanted == null) statsData.PlantsPlanted = new Dictionary<ulong, int>();
            }
            catch { statsData = new StatsData(); }
        }

        private void LoadProgressData()
        {
            try
            {
                progressData = Interface.Oxide.DataFileSystem.ReadObject<ProgressData>("TwinxHUD_Progress") ?? new ProgressData();
                if (progressData.Players == null) progressData.Players = new Dictionary<ulong, ProgressRecord>();
            }
            catch { progressData = new ProgressData(); }
        }

        private void SaveProgressData()
        {
            Interface.Oxide.DataFileSystem.WriteObject("TwinxHUD_Progress", progressData);
        }

        private void SyncAllProgress()
        {
            foreach (BasePlayer player in BasePlayer.activePlayerList)
                if (player != null && player.IsConnected) SyncProgress(player);
        }

        private void SaveStatsData()
        {
            Interface.Oxide.DataFileSystem.WriteObject("TwinxHUD_Stats", statsData);
        }

        private int SavedValue(Dictionary<ulong, int> values, ulong id)
        {
            int value;
            return values != null && values.TryGetValue(id, out value) ? value : 0;
        }

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || info == null) return;
            BasePlayer attacker = info.InitiatorPlayer;
            if (attacker == null || attacker.IsNpc) return;

            if (IsWildAnimal(entity))
            {
                int value = SavedValue(statsData.AnimalKills, attacker.userID);
                statsData.AnimalKills[attacker.userID] = value + 1;
                SaveStatsData();
            }
        }

        // Count every player-planted growable: planter boxes, terrain soil,
        // seeds and cuttings/clones. OnEntityBuilt only fires for player placement,
        // so naturally spawned plants are not counted.
        private void OnEntityBuilt(Planner plan, GameObject objectBlock)
        {
            if (plan == null || objectBlock == null) return;

            BasePlayer player = plan.GetOwnerPlayer();
            if (player == null || player.IsNpc) return;

            // Rust exposes the planted GrowableEntity as a component on the
            // spawned GameObject. Do not require plant.planter: ground plants
            // have no PlanterBox parent and would otherwise never be counted.
            GrowableEntity plant = objectBlock.GetComponent<GrowableEntity>();
            if (plant == null) return;

            NextTick(() =>
            {
                if (plant == null || plant.IsDestroyed) return;

                // Network ID prevents the same placement from being counted twice.
                if (plant.net != null && !countedPlants.Add(plant.net.ID.Value)) return;

                int value = SavedValue(statsData.PlantsPlanted, player.userID);
                statsData.PlantsPlanted[player.userID] = value + 1;
                SaveStatsData();
            });
        }

        private bool IsWildAnimal(BaseCombatEntity entity)
        {
            if (entity == null) return false;
            if (entity is BaseAnimalNPC) return true;

            string prefab = (entity.PrefabName ?? entity.name ?? string.Empty).ToLowerInvariant();

            return prefab.Contains("/boar/")
                || prefab.Contains("/bear/")
                || prefab.Contains("/stag/")
                || prefab.Contains("/chicken/")
                || prefab.Contains("/wolf/")
                || prefab.Contains("/horse/")
                || prefab.Contains("/shark/");
        }

        private void OpenStatsCommand(BasePlayer player, string command, string[] args)
        {
            OpenTabFromChatCommand(player, "stats");
        }

        private void OpenTopCommand(BasePlayer player, string command, string[] args)
        {
            OpenTabFromChatCommand(player, "top");
        }

        private void OpenTeamStatsCommand(BasePlayer player, string command, string[] args)
        {
            OpenTabFromChatCommand(player, "team");
        }

        private void OpenEventsCommand(BasePlayer player, string command, string[] args)
        {
            OpenTabFromChatCommand(player, "events");
        }

        private void OpenTabFromChatCommand(BasePlayer player, string tab)
        {
            if (player == null) return;
            if (!player.IsAdmin && !permission.UserHasPermission(player.UserIDString, PermUse))
            {
                SendReply(player, "Нет доступа. Выдайте разрешение: oxide.grant user " + player.UserIDString + " twinxhud.use");
                return;
            }

            opened.Add(player.userID);
            currentTabs[player.userID] = tab;
            DrawMain(player, tab);
        }

        private BasePlayer GetPlayer(ConsoleSystem.Arg arg)
        {
            if (arg == null || arg.Connection == null) return null;
            return arg.Connection.player as BasePlayer;
        }

        [ConsoleCommand("twxui.close")]
        private void CloseCommand(ConsoleSystem.Arg arg)
        {
            BasePlayer p = GetPlayer(arg);
            if (p == null) return;
            opened.Remove(p.userID);
            CuiHelper.DestroyUi(p, Root);
            DrawLauncher(p);
        }

        [ConsoleCommand("twxui.toggle")]
        private void ToggleCommand(ConsoleSystem.Arg arg)
        {
            BasePlayer p = GetPlayer(arg);
            if (p == null) return;
            if (opened.Contains(p.userID))
            {
                opened.Remove(p.userID);
                CuiHelper.DestroyUi(p, Root);
                DrawLauncher(p);
            }
            else
            {
                opened.Add(p.userID);
                currentTabs[p.userID] = "stats";
                DrawMain(p, "stats");
            }
        }

        [ConsoleCommand("twxui.topcat")]
        private void TopCategoryCommand(ConsoleSystem.Arg arg)
        {
            BasePlayer p = GetPlayer(arg);
            if (p == null) return;
            string category = arg.GetString(0, "PVPKills");
            if (!TopCategories.ContainsKey(category)) category = "PVPKills";
            currentTopCategories[p.userID] = category;
            opened.Add(p.userID);
            currentTabs[p.userID] = "top";
            DrawMain(p, "top");
        }

        [ConsoleCommand("twxui.tab")]
        private void TabCommand(ConsoleSystem.Arg arg)
        {
            BasePlayer p = GetPlayer(arg);
            if (p == null) return;
            string tab = arg.GetString(0, "stats").ToLowerInvariant();
            string[] allowed = { "stats", "top", "team", "events", "awards", "cases", "achievements", "shop" };
            if (!allowed.Contains(tab)) tab = "stats";
            opened.Add(p.userID);
            currentTabs[p.userID] = tab;
            DrawMain(p, tab);
        }

        private void DrawLauncher(BasePlayer p)
        {
            CuiHelper.DestroyUi(p, Launcher);
            CuiHelper.DestroyUi(p, Launcher + ".CoinHud");
            CuiHelper.DestroyUi(p, "TwinxHUD.CoinBalance");
            CuiHelper.DestroyUi(p, "TwinxHUD.CoinLabel");
            if (opened.Contains(p.userID)) return;
            CuiElementContainer c = new CuiElementContainer();
            AddImage(c, "Overlay", Launcher, "open", "0.012 0.025", "0.055 0.067");
            AddButton(c, "Overlay", Launcher + ".Hit", "", "twxui.toggle", "0.012 0.025", "0.055 0.067");
            // Coin balance is displayed only on the Coin panel inside the opened menu.
            CuiHelper.AddUi(p, c);
        }

        private long GetCoinBalance(BasePlayer p)
        {
            if (p == null) return 0;
            try
            {
                CoinDataMirror coinData = Interface.Oxide.DataFileSystem.ReadObject<CoinDataMirror>("TwinxCoin_Data");
                if (coinData == null || coinData.Wallets == null) return 0;

                CoinWalletMirror wallet;
                if (!coinData.Wallets.TryGetValue(p.userID, out wallet) || wallet == null)
                    return 0;

                return Math.Max(0L, wallet.Balance);
            }
            catch (Exception ex)
            {
                PrintWarning("Не удалось прочитать TwinxCoin_Data: " + ex.Message);
                return 0;
            }
        }

        private string FormatCoinBalance(BasePlayer p)
        {
            return GetCoinBalance(p).ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
        }

        private void DrawMenuCoin(CuiElementContainer c, BasePlayer p)
        {
            // Coin panel is displayed at 150px wide, preserving the source 252:75 ratio.
            // Resulting height: 44.64px.
            AddImageFixedSize(c, Root, Root + ".CoinPanel", "coin_panel", 0.800f, 0.879f, 150f, 44.64f);

            // Balance font automatically decreases for longer values so the text stays inside the panel.
            string coinBalance = FormatCoinBalance(p);
            int coinBalanceFontSize = Math.Max(11, 17 - Math.Max(0, coinBalance.Length - 5));
            AddTextOnPanelBold(c, Root, coinBalance, coinBalanceFontSize, "1 1 1 1",
                TextAnchor.MiddleRight, 0.810f, 0.878f, -73f, -22.3f, 20f, 22.3f);
            AddTextOnPanelBold(c, Root, "COIN", 12, "1 0.48 0.03 1",
                TextAnchor.MiddleLeft, 0.810f, 0.878f, 24f, -22.3f, 74f, 22.3f);
        }

        private void RefreshCoinDisplay(BasePlayer p)
        {
            if (p == null || !p.IsConnected) return;

            // Coin balance is part of the menu's root UI. Rebuild the menu only
            // once; adding the Coin elements separately before DrawMain caused
            // duplicate CUI elements to remain layered over one another.
            if (!opened.Contains(p.userID)) return;

            string tab;
            if (!currentTabs.TryGetValue(p.userID, out tab) || string.IsNullOrEmpty(tab))
                tab = "stats";

            DrawMain(p, tab);
        }

        private void OnTwinxCoinBalanceChanged(ulong userId, long balance)
        {
            BasePlayer player = BasePlayer.FindByID(userId);
            if (player != null && player.IsConnected) RefreshCoinDisplay(player);
        }

        private void DestroyAll(BasePlayer p)
        {
            if (p == null) return;
            CuiHelper.DestroyUi(p, Root);
            CuiHelper.DestroyUi(p, Launcher);
            CuiHelper.DestroyUi(p, Launcher + ".CoinHud");
            CuiHelper.DestroyUi(p, "TwinxHUD.CoinBalance");
            CuiHelper.DestroyUi(p, "TwinxHUD.CoinLabel");
        }

        private void DrawMain(BasePlayer p, string tab)
        {
            CuiHelper.DestroyUi(p, Root);
            CuiHelper.DestroyUi(p, Launcher);
            CuiHelper.DestroyUi(p, Launcher + ".CoinHud");
            CuiHelper.DestroyUi(p, "TwinxHUD.CoinBalance");
            CuiHelper.DestroyUi(p, "TwinxHUD.CoinLabel");
            CuiElementContainer c = new CuiElementContainer();

            c.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                CursorEnabled = true
            }, "Hud", Root);

            AddImage(c, Root, Root + ".MainBg", "main", "0.080 0.075", "0.920 0.925");
            AddImage(c, Root, Root + ".Sidebar", "sidebar", "0.080 0.075", "0.226 0.925");
            if (tab == "stats")
            {
                AddImage(c, Root, Root + ".Profile", "profile", "0.238 0.607", "0.912 0.805");
                AddImage(c, Root, Root + ".Bottom", "bottom", "0.238 0.139", "0.912 0.291");
            }
            AddImage(c, Root, Root + ".Close", "close", "0.874 0.856", "0.907 0.902");
            AddButton(c, Root, Root + ".CloseHit", "", "twxui.close", "0.874 0.856", "0.907 0.902");
            DrawMenuCoin(c, p);

            // Верхний заголовок страницы статистики.
            if (tab == "stats")
            {
                AddText(c, Root, "TWINX RUST  •  DUO  •  X1", 15, "0.86 0.87 0.90 1", TextAnchor.MiddleLeft,
                    "0.258 0.867", "0.620 0.902");
                AddTextBold(c, Root, "МОЯ СТАТИСТИКА", 24, "1 1 1 1", TextAnchor.MiddleLeft,
                    "0.258 0.815", "0.620 0.865");
            }

            DrawNavigation(c, tab);
            if (tab == "stats") DrawStats(c, p);
            else if (tab == "top") DrawTop(c, p);
            else if (tab == "team") DrawTeam(c, p);
            else if (tab == "shop") DrawShop(c);
            else DrawFeaturePage(c, tab);

            CuiHelper.AddUi(p, c);
        }

        private void DrawNavigation(CuiElementContainer c, string active)
        {
            string[] keys = { "stats", "top", "events", "awards", "cases", "achievements", "shop" };
            string[] labels = { "СТАТИСТИКА", "ТОП", "СОБЫТИЯ", "НАГРАДЫ", "КЕЙСЫ", "ДОСТИЖЕНИЯ", "МАГАЗИН" };
            string[] icons = { "icon_stats", "icon_top", "icon_events", "icon_rewards", "icon_cases", "icon_achievements", "icon_shop" };
            float[] ys = { 0.760f, 0.680f, 0.600f, 0.520f, 0.440f, 0.360f, 0.280f };

            for (int i = 0; i < keys.Length; i++)
            {
                string key = keys[i];
                string minY = (ys[i] - 0.026f).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
                string maxY = (ys[i] + 0.026f).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);

                AddImage(c, Root, Root + ".NavBg." + key, key == active ? "active" : "normal",
                    "0.084 " + minY, "0.223 " + maxY);

                // Иконка и подпись собраны плотнее и центрированы внутри кнопки.
                AddImage(c, Root, Root + ".NavIcon." + key, icons[i],
                    XY(0.101f, ys[i] - 0.016f), XY(0.119f, ys[i] + 0.016f));

                AddTextBold(c, Root, labels[i], 12, "1 1 1 1", TextAnchor.MiddleCenter,
                    "0.101 " + minY, "0.209 " + maxY);

                AddButton(c, Root, Root + ".NavHit." + key, "", "twxui.tab " + key,
                    "0.084 " + minY, "0.223 " + maxY);
            }
        }

        private void DrawStats(CuiElementContainer c, BasePlayer p)
        {
            double kills = Stat(p, "PVPKills");
            double deaths = Stat(p, "Deaths");
            double npc = Stat(p, "NPCKills");
            double kd = Stat(p, "KDR");
            if (kd <= 0 && deaths > 0) kd = kills / deaths;
            double shots = Stat(p, "BulletsFired") + Stat(p, "ArrowsFired");
            double heads = Stat(p, "HeadShots");
            double heli = Stat(p, "HeliKills");
            double apc = Stat(p, "APCKills");
            double animalKills = SavedValue(statsData.AnimalKills, p.userID);
            double plantsPlanted = SavedValue(statsData.PlantsPlanted, p.userID);
            double built = Stat(p, "StructuresBuilt");
            double upgraded = Stat(p, "StructuresUpgraded");
            double demolished = Stat(p, "StructuresDemolished");
            double crafted = Stat(p, "ItemsCrafted");
            double deployed = Stat(p, "ItemsDeployed");
            double plantsGathered = Stat(p, "PlantsGathered");
            double seconds = Playtime(p);

            // Steam avatar inside the framed square on the profile banner.
            AddPlayerAvatar(c, p);

            AddTextBold(c, Root, p.displayName ?? "Игрок", 23, "1 1 1 1", TextAnchor.MiddleLeft, "0.345 0.732", "0.585 0.770");
            AddText(c, Root, "STEAM ID  " + p.UserIDString, 11, "0.86 0.87 0.90 1", TextAnchor.MiddleLeft, "0.345 0.704", "0.585 0.740");
            AddTextBold(c, Root, "Игрок", 22, "1 0.48 0.03 1", TextAnchor.MiddleLeft, "0.345 0.633", "0.515 0.701");

            ProgressRecord progress = SyncProgress(p);
            double xpTotal = progress == null ? 0 : progress.TotalXp;
            int level; double levelXp, nextXp;
            CalculateLevel(xpTotal, out level, out levelXp, out nextXp);
            AddText(c, Root, "УРОВЕНЬ", 15, "1 1 1 1", TextAnchor.MiddleCenter, "0.765 0.745", "0.895 0.780");
            AddTextBold(c, Root, level.ToString(), 36, "1 0.48 0.03 1", TextAnchor.MiddleCenter, "0.755 0.692", "0.905 0.752");
            AddText(c, Root, F(levelXp) + " / " + F(nextXp) + " XP", 10, "1 1 1 1", TextAnchor.MiddleCenter, "0.765 0.645", "0.895 0.665");
            AddImage(c, Root, Root + ".XpBg", "xpbg", "0.7675 0.669", "0.8925 0.686");

            float ratio = nextXp <= 0 ? 0 : (float)Math.Max(0, Math.Min(1, levelXp / nextXp));
            if (ratio > 0.001f)
                AddImage(c, Root, Root + ".XpFill", "xpfill", "0.7675 0.669",
                    "" + (0.7675f + 0.125f * ratio).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + " 0.686");

            // Первая карточка не менялась. Остальные сдвинуты влево с шагом 0.3%.
            DrawStatCard(c, "stat_playtime", "ВРЕМЯ В ИГРЕ", TimeText(seconds), 0.2520f, 0.5410f, 0.3670f, 0.6210f);
            DrawStatCard(c, "stat_kills", "УБИЙСТВА", F(kills), 0.3840f, 0.5410f, 0.4990f, 0.6210f);
            DrawStatCard(c, "stat_deaths", "СМЕРТИ", F(deaths), 0.5160f, 0.5410f, 0.6310f, 0.6210f);
            DrawStatCard(c, "stat_kd", "K/D", kd.ToString("0.00"), 0.6480f, 0.5410f, 0.7630f, 0.6210f);
            DrawStatCard(c, "stat_npc", "УБИЙСТВА NPC", F(npc), 0.7810f, 0.5410f, 0.8960f, 0.6210f);

            AddImageFixedSize(c, Root, Root + ".Pvp", "pvp", 0.3455f, 0.4145f, 250f, 157.333f);
            AddImageFixedSize(c, Root, Root + ".Pve", "pve", 0.5725f, 0.4145f, 250f, 157.333f);
            AddImageFixedSize(c, Root, Root + ".Activity", "activity", 0.7970f, 0.4145f, 250f, 157.333f);
            // Текст привязан к центру каждой панели: одинаковые отступы и точное совпадение с PNG.
            AddTextOnPanelBold(c, Root, "PvP", 24, "1 0.50 0.03 1", TextAnchor.UpperLeft, 0.3455f, 0.4045f, -115f, 30f, 115f, 78f);
            AddTextOnPanel(c, Root, "Убийства: " + F(kills), 11, "0.93 0.93 0.93 1", TextAnchor.UpperLeft, 0.3455f, 0.4045f, -115f, 25f, 115f, 42f);
            AddTextOnPanel(c, Root, "Смерти: " + F(deaths), 11, "0.93 0.93 0.93 1", TextAnchor.UpperLeft, 0.3455f, 0.4045f, -115f, 7.5f, 115f, 24.5f);
            AddTextOnPanel(c, Root, "K/D: " + kd.ToString("0.00"), 11, "0.93 0.93 0.93 1", TextAnchor.UpperLeft, 0.3455f, 0.4045f, -115f, -10f, 115f, 7f);
            AddTextOnPanel(c, Root, "Выстрелы: " + F(shots), 11, "0.93 0.93 0.93 1", TextAnchor.UpperLeft, 0.3455f, 0.4045f, -115f, -27.5f, 115f, -10.5f);
            AddTextOnPanel(c, Root, "Хедшоты: " + F(heads), 11, "0.93 0.93 0.93 1", TextAnchor.UpperLeft, 0.3455f, 0.4045f, -115f, -45f, 115f, -28f);

            AddTextOnPanelBold(c, Root, "PvE", 24, "1 0.50 0.03 1", TextAnchor.UpperLeft, 0.5725f, 0.4045f, -115f, 30f, 115f, 78f);
            AddTextOnPanel(c, Root, "Убийства NPC: " + F(npc), 11, "0.93 0.93 0.93 1", TextAnchor.UpperLeft, 0.5725f, 0.4045f, -115f, 25f, 115f, 40f);
            AddTextOnPanel(c, Root, "Танк: " + F(apc), 11, "0.93 0.93 0.93 1", TextAnchor.UpperLeft, 0.5725f, 0.4045f, -115f, 10f, 115f, 25f);
            AddTextOnPanel(c, Root, "Вертолёт: " + F(heli), 11, "0.93 0.93 0.93 1", TextAnchor.UpperLeft, 0.5725f, 0.4045f, -115f, -5f, 115f, 10f);
            AddTextOnPanel(c, Root, "Убийства животных: " + F(animalKills), 11, "0.93 0.93 0.93 1", TextAnchor.UpperLeft, 0.5725f, 0.4045f, -115f, -20f, 115f, -5f);
            AddTextOnPanel(c, Root, "Посажено растений: " + F(plantsPlanted), 11, "0.93 0.93 0.93 1", TextAnchor.UpperLeft, 0.5725f, 0.4045f, -115f, -35f, 115f, -20f);

            AddTextOnPanelBold(c, Root, "АКТИВНОСТЬ", 24, "1 0.50 0.03 1", TextAnchor.UpperLeft, 0.7970f, 0.4045f, -115f, 30f, 115f, 78f);
            AddTextOnPanel(c, Root, "Построено: " + F(built), 11, "0.93 0.93 0.93 1", TextAnchor.UpperLeft, 0.7970f, 0.4045f, -115f, 25f, 115f, 42f);
            AddTextOnPanel(c, Root, "Создано предметов: " + F(crafted), 11, "0.93 0.93 0.93 1", TextAnchor.UpperLeft, 0.7970f, 0.4045f, -115f, 7.5f, 115f, 24.5f);
            AddTextOnPanel(c, Root, "Установлено: " + F(deployed), 11, "0.93 0.93 0.93 1", TextAnchor.UpperLeft, 0.7970f, 0.4045f, -115f, -10f, 115f, 7f);
            AddTextOnPanel(c, Root, "Время в игре: " + TimeText(seconds), 11, "0.93 0.93 0.93 1", TextAnchor.UpperLeft, 0.7970f, 0.4045f, -115f, -27.5f, 115f, -10.5f);

            // Нижний баннер: сезонное место игрока слева, лучшие показатели справа.
            int playerRank = GetPlayerRank(p.userID, "PVPKills");
            AddText(c, Root, "│", 34, "1 1 1 1", TextAnchor.MiddleCenter, "0.505 0.150", "0.525 0.280");
            AddTextBold(c, Root, "ТВОЁ МЕСТО В РЕЙТИНГЕ", 14, "1 1 1 1", TextAnchor.MiddleLeft, "0.325 0.225", "0.510 0.275");
            AddTextBold(c, Root, playerRank > 0 ? "#" + playerRank : "—", 41, "1 0.48 0.03 1", TextAnchor.MiddleLeft, "0.325 0.160", "0.450 0.240");

            AddTextBold(c, Root, "ЛУЧШИЕ РЕЗУЛЬТАТЫ", 15, "1 0.48 0.03 1", TextAnchor.MiddleLeft, "0.575 0.230", "0.830 0.275");
            AddText(c, Root, "— Убийства: " + F(GetTopScore("PVPKills")), 13, "1 1 1 1", TextAnchor.MiddleLeft, "0.575 0.190", "0.705 0.235");
            AddText(c, Root, "— NPC: " + F(GetTopScore("NPCKills")), 13, "1 1 1 1", TextAnchor.MiddleLeft, "0.705 0.190", "0.845 0.235");
            AddText(c, Root, "— K/D: " + GetTopScore("KDR").ToString("0.00"), 13, "1 1 1 1", TextAnchor.MiddleLeft, "0.575 0.165", "0.705 0.210");
            AddText(c, Root, "— Твоё время: " + TimeText(seconds), 13, "1 1 1 1", TextAnchor.MiddleLeft, "0.705 0.165", "0.865 0.210");
        }

        private int GetPlayerRank(ulong userId, string category)
        {
            if (PlayerRanks == null) return 0;
            try
            {
                object raw = PlayerRanks.Call("GetTop", category, 10000, null);
                var top = raw as Dictionary<ulong, Dictionary<string, object>>;
                if (top == null || top.Count == 0) return 0;

                var ordered = top
                    .Where(row => row.Value != null && row.Value.ContainsKey(category))
                    .Select(row => new
                    {
                        UserId = row.Key,
                        Score = ToNumber(row.Value[category])
                    })
                    .OrderByDescending(row => row.Score)
                    .ThenBy(row => row.UserId)
                    .ToList();

                int rank = 1;
                foreach (var row in ordered)
                {
                    if (row.UserId == userId) return rank;
                    rank++;
                }
            }
            catch (Exception ex)
            {
                PrintWarning("Не удалось получить место игрока в PlayerRanks: " + ex.Message);
            }
            return 0;
        }

        private double GetTopScore(string category)
        {
            if (PlayerRanks == null) return 0;
            try
            {
                object raw = PlayerRanks.Call("GetTop", category, 1, null);
                var top = raw as Dictionary<ulong, Dictionary<string, object>>;
                if (top == null || top.Count == 0) return 0;
                var row = top.OrderByDescending(entry => entry.Value != null && entry.Value.ContainsKey(category)
                    ? ToNumber(entry.Value[category]) : 0).First();
                object value;
                return row.Value != null && row.Value.TryGetValue(category, out value) ? ToNumber(value) : 0;
            }
            catch (Exception ex)
            {
                PrintWarning("Не удалось получить рекорд PlayerRanks (" + category + "): " + ex.Message);
                return 0;
            }
        }

        private double ToNumber(object value)
        {
            if (value == null) return 0;
            double parsed;
            return double.TryParse(Convert.ToString(value), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out parsed) ? parsed : 0;
        }

        private void DrawStatCard(CuiElementContainer c, string iconKey, string title, string value, float x1, float y1, float x2, float y2)
        {
            AddImage(c, Root, Root + ".Card." + x1, "card", XY(x1, y1), XY(x2, y2));

            // Фиксированный размер в пикселях: 31x31. AnchorMin/Max в долях
            // экрана искажают квадратные иконки на широкоформатных мониторах.
            float iconCenterX = x1 + 0.021f;
            float iconCenterY = (y1 + y2) / 2f;
            AddImageFixedSize(c, Root, Root + ".CardIcon." + iconKey, iconKey,
                iconCenterX, iconCenterY, 31f, 31f);

            // Одинаковая сетка для всех пяти карточек: заголовок в верхней зоне,
            // значение в нижней зоне, иконка строго по вертикальному центру.
            // Фиксированные отступы не зависят от ширины конкретной подписи.
            AddText(c, Root, title, 9, "0.88 0.88 0.90 1", TextAnchor.MiddleLeft,
                XY(x1 + 0.038f, y1 + 0.040f), XY(x2 - 0.004f, y1 + 0.068f));
            AddText(c, Root, value, 16, "1 1 1 1", TextAnchor.MiddleLeft,
                XY(x1 + 0.038f, y1 + 0.013f), XY(x2 - 0.004f, y1 + 0.044f));
        }

        private static readonly Dictionary<string, string> TopCategories = new Dictionary<string, string>
        {
            ["PVPKills"] = "PvP",
            ["NPCKills"] = "PvE",
            ["ResourcesGathered"] = "РЕСУРСЫ",
            ["StructuresBuilt"] = "СТРОИТЕЛЬСТВО",
            ["TimePlayed"] = "ВРЕМЯ В ИГРЕ"
        };

        private static readonly Dictionary<string, string> TopCategoryIcons = new Dictionary<string, string>
        {
            ["PVPKills"] = "top_icon_pvp",
            ["NPCKills"] = "top_icon_pve",
            ["ResourcesGathered"] = "top_icon_resources",
            ["StructuresBuilt"] = "top_icon_build",
            ["TimePlayed"] = "top_icon_time"
        };

        private void AddSolidPanel(CuiElementContainer c, string name, string color, string min, string max)
        {
            c.Add(new CuiPanel
            {
                Image = { Color = color },
                RectTransform = { AnchorMin = min, AnchorMax = max }
            }, Root, name);
        }

        private void RefreshTopPages()
        {
            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                if (player == null || !player.IsConnected) continue;
                string tab;
                if (currentTabs.TryGetValue(player.userID, out tab) && tab == "top")
                    DrawMain(player, "top");
            }
        }

        private string GetWipeCountdown()
        {
            // Twinx weekly map wipe: Thursday 17:00 Moscow time (UTC+3).
            DateTime moscowNow = DateTime.UtcNow.AddHours(3);
            int days = ((int)DayOfWeek.Thursday - (int)moscowNow.DayOfWeek + 7) % 7;
            DateTime nextWipe = moscowNow.Date.AddDays(days).AddHours(17);
            if (nextWipe <= moscowNow) nextWipe = nextWipe.AddDays(7);
            TimeSpan left = nextWipe - moscowNow;
            return left.Days + "д  " + left.Hours + "ч  " + left.Minutes + "м";
        }
        private void DrawTop(CuiElementContainer c, BasePlayer viewer)
        {
            AddText(c, Root, "TWINX RUST  •  DUO  •  X1", 15, "0.86 0.87 0.90 1",
                TextAnchor.MiddleLeft, "0.205 0.867", "0.620 0.902");
            AddTextBold(c, Root, "ТОП ИГРОКОВ", 25, "1 1 1 1",
                TextAnchor.MiddleLeft, "0.205 0.805", "0.620 0.858");

            string selected = "PVPKills";
            if (viewer != null && currentTopCategories.ContainsKey(viewer.userID))
                selected = currentTopCategories[viewer.userID];
            if (!TopCategories.ContainsKey(selected)) selected = "PVPKills";

            // Five evenly spaced tabs with rounded background layers, sized to the supplied reference.
            int buttonIndex = 0;
            string[] categoryOrder = { "PVPKills", "NPCKills", "ResourcesGathered", "StructuresBuilt", "TimePlayed" };
            foreach (string categoryKey in categoryOrder)
            {
                var category = new KeyValuePair<string, string>(categoryKey, TopCategories[categoryKey]);
                float x1 = 0.205f + buttonIndex * 0.154f;
                float x2 = x1 + 0.149f;
                bool active = selected == category.Key;
                AddImage(c, Root, Root + ".TopCatBg." + category.Key,
                    active ? "active" : "normal", XY(x1, 0.680f), XY(x2, 0.770f));
                AddImageFixedSize(c, Root, Root + ".TopCatIcon." + category.Key,
                    TopCategoryIcons[category.Key], x1 + 0.026f, 0.735f, 29f, 29f);
                AddTextBold(c, Root, category.Value, 12,
                    active ? "1 0.72 0.28 1" : "0.96 0.96 0.96 1",
                    TextAnchor.MiddleCenter, XY(x1 + 0.045f, 0.716f), XY(x2 - 0.004f, 0.757f));
                string subtitle = category.Key == "PVPKills" ? "Убийства игроков" :
                    category.Key == "NPCKills" ? "Убийства NPC" :
                    category.Key == "ResourcesGathered" ? "Добыто ресурсов" :
                    category.Key == "StructuresBuilt" ? "Построено объектов" : "Онлайн на сервере";
                AddText(c, Root, subtitle, 10, "0.72 0.73 0.75 1",
                    TextAnchor.MiddleCenter, XY(x1 + 0.003f, 0.684f), XY(x2 - 0.003f, 0.718f));
                AddButton(c, Root, Root + ".TopCatHit." + category.Key, "",
                    "twxui.topcat " + category.Key, XY(x1, 0.680f), XY(x2, 0.770f));
                buttonIndex++;
            }

            if (PlayerRanks == null)
            {
                AddText(c, Root, "Плагин PlayerRanks не загружен.", 16,
                    "1 0.5 0.2 1", TextAnchor.MiddleCenter,
                    "0.260 0.300", "0.890 0.390");
                return;
            }

            // Main leaderboard panel.
            AddSolidPanel(c, Root + ".TopTableBorder", "0.34 0.35 0.37 0.96",
                "0.200 0.105", "0.685 0.661");
            AddSolidPanel(c, Root + ".TopTable", "0.035 0.038 0.043 0.97",
                "0.202 0.107", "0.683 0.659");

            bool pvp = selected == "PVPKills";
            if (pvp)
            {
                AddTextBold(c, Root, "#", 11, "0.85 0.85 0.85 1", TextAnchor.MiddleCenter,
                    "0.255 0.615", "0.285 0.650");
                AddTextBold(c, Root, "ИГРОК", 11, "0.85 0.85 0.85 1", TextAnchor.MiddleLeft,
                    "0.292 0.615", "0.455 0.650");
                AddTextBold(c, Root, "УБИЙСТВА", 10, "0.85 0.85 0.85 1", TextAnchor.MiddleCenter,
                    "0.475 0.615", "0.545 0.650");
                AddTextBold(c, Root, "СМЕРТИ", 10, "0.85 0.85 0.85 1", TextAnchor.MiddleCenter,
                    "0.545 0.615", "0.615 0.650");
                AddTextBold(c, Root, "K/D", 11, "0.85 0.85 0.85 1", TextAnchor.MiddleCenter,
                    "0.615 0.615", "0.680 0.650");
            }
            else
            {
                AddTextBold(c, Root, "#", 11, "0.85 0.85 0.85 1", TextAnchor.MiddleCenter,
                    "0.255 0.615", "0.285 0.650");
                AddTextBold(c, Root, "ИГРОК", 11, "0.85 0.85 0.85 1", TextAnchor.MiddleLeft,
                    "0.292 0.615", "0.455 0.650");
                string scoreHeader = selected == "NPCKills" ? "УБИЙСТВА NPC" :
                    selected == "ResourcesGathered" ? "ДОБЫТО РЕСУРСОВ" :
                    selected == "StructuresBuilt" ? "ПОСТРОЕНО ОБЪЕКТОВ" : "ВРЕМЯ В ИГРЕ";
                AddTextBold(c, Root, scoreHeader, 11, "0.85 0.85 0.85 1", TextAnchor.MiddleRight,
                    "0.475 0.615", "0.680 0.650");
            }

            Dictionary<ulong, Dictionary<string, object>> top = null;
            try
            {
                object raw = PlayerRanks.Call("GetTop", selected, 10, null);
                top = raw as Dictionary<ulong, Dictionary<string, object>>;
            }
            catch (Exception ex)
            {
                PrintWarning("Не удалось получить топ " + selected + ": " + ex.Message);
            }

            List<KeyValuePair<ulong, Dictionary<string, object>>> rows =
                top == null ? new List<KeyValuePair<ulong, Dictionary<string, object>>>() :
                top.Where(row => row.Value != null)
                   .OrderByDescending(row =>
                   {
                       object value;
                       return row.Value.TryGetValue(selected, out value) ? ToNumber(value) : 0;
                   })
                   .Take(10).ToList();

            if (rows.Count == 0)
            {
                AddText(c, Root, "Пока нет данных рейтинга по этой категории.", 14,
                    "0.9 0.9 0.9 1", TextAnchor.MiddleCenter,
                    "0.260 0.400", "0.680 0.550");
            }
            else
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    var row = rows[i];
                    float yTop = 0.608f - i * 0.047f;
                    float yBottom = yTop - 0.043f;
                    string rowColor = i == 0 ? "0.30 0.19 0.045 0.98" :
                                      i == 1 ? "0.20 0.20 0.21 0.98" :
                                      i == 2 ? "0.25 0.14 0.06 0.98" :
                                      (i % 2 == 0 ? "0.10 0.11 0.12 0.90" : "0.14 0.15 0.16 0.90");
                    string rowBorder = i == 0 ? "1 0.43 0.015 0.98" :
                                       i == 1 ? "0.55 0.57 0.60 0.95" :
                                       i == 2 ? "0.72 0.34 0.08 0.95" : "0.22 0.23 0.25 0.90";
                    AddSolidPanel(c, Root + ".TopRowBorder." + i, rowBorder,
                        XY(0.207f, yBottom - 0.001f), XY(0.678f, yTop + 0.001f));
                    AddSolidPanel(c, Root + ".TopRow." + i, rowColor,
                        XY(0.209f, yBottom + 0.001f), XY(0.676f, yTop - 0.001f));

                    if (i == 0)
                        AddImageFixedSize(c, Root, Root + ".TopMedal." + i, "top_medal_gold", 0.231f, (yBottom + yTop) / 2f, 34f, 34f);
                    else if (i == 1)
                        AddImageFixedSize(c, Root, Root + ".TopMedal." + i, "top_medal_silver", 0.231f, (yBottom + yTop) / 2f, 34f, 34f);
                    else if (i == 2)
                        AddImageFixedSize(c, Root, Root + ".TopMedal." + i, "top_medal_bronze", 0.231f, (yBottom + yTop) / 2f, 34f, 34f);
                    else
                        AddTextBold(c, Root, (i + 1).ToString(), 12, "0.95 0.95 0.95 1",
                            TextAnchor.MiddleCenter, XY(0.214f, yBottom), XY(0.248f, yTop));
                    AddTopAvatar(c, row.Key, i, yBottom, yTop, 0.270f, 18f);

                    object nameValue;
                    string name = row.Value.TryGetValue("Name", out nameValue) && nameValue != null
                        ? Convert.ToString(nameValue) : row.Key.ToString();
                    AddText(c, Root, name, 12, "1 1 1 1", TextAnchor.MiddleLeft,
                        XY(0.296f, yBottom), XY(0.465f, yTop));

                    double score = 0;
                    object scoreValue;
                    if (row.Value.TryGetValue(selected, out scoreValue)) score = ToNumber(scoreValue);
                    if (pvp)
                    {
                        double deaths = 0, kd = 0;
                        object value;
                        if (row.Value.TryGetValue("Deaths", out value)) deaths = ToNumber(value);
                        if (row.Value.TryGetValue("KDR", out value)) kd = ToNumber(value);
                        if (kd <= 0 && deaths > 0) kd = score / deaths;
                        AddTextBold(c, Root, F(score), 12, "1 1 1 1", TextAnchor.MiddleCenter,
                            XY(0.475f, yBottom), XY(0.545f, yTop));
                        AddTextBold(c, Root, F(deaths), 12, "1 1 1 1", TextAnchor.MiddleCenter,
                            XY(0.545f, yBottom), XY(0.615f, yTop));
                        AddTextBold(c, Root, kd.ToString("0.00"), 12, "1 1 1 1", TextAnchor.MiddleCenter,
                            XY(0.615f, yBottom), XY(0.675f, yTop));
                    }
                    else
                    {
                        string scoreText = selected == "TimePlayed" ? TimeText(score) : F(score);
                        AddTextBold(c, Root, scoreText, 12, "1 0.58 0.10 1", TextAnchor.MiddleRight,
                            XY(0.515f, yBottom), XY(0.675f, yTop));
                    }
                }
            }

            // Right side: personal position and current score.
            AddSolidPanel(c, Root + ".MyRankPanelBorder", "0.34 0.35 0.37 0.96",
                "0.695 0.424", "0.970 0.661");
            AddSolidPanel(c, Root + ".MyRankPanel", "0.055 0.060 0.068 0.98",
                "0.697 0.426", "0.968 0.659");
            AddTextBold(c, Root, "👤  ТВОЯ ПОЗИЦИЯ", 15, "1 0.55 0.08 1",
                TextAnchor.MiddleLeft, "0.710 0.610", "0.950 0.650");

            int personalRank = GetPlayerRank(viewer == null ? 0UL : viewer.userID, selected);
            double personalScore = GetPlayerCategoryScore(viewer, selected);
            string rankText = personalRank > 0 ? "#" + personalRank : "—";
            string personalScoreText = selected == "TimePlayed" ? TimeText(personalScore) : F(personalScore);

            if (viewer != null) AddTopAvatar(c, viewer.userID, 99, 0.455f, 0.555f, 0.735f, 36f);
            AddSolidPanel(c, Root + ".MyRankDivider", "0.42 0.43 0.45 0.90",
                "0.865 0.447", "0.866 0.585");
            AddTextBold(c, Root, rankText, 34, "1 0.48 0.03 1",
                TextAnchor.MiddleCenter, "0.780 0.490", "0.865 0.590");
            AddTextBold(c, Root, viewer == null ? "Игрок" : viewer.displayName, 12,
                "1 1 1 1", TextAnchor.MiddleCenter, "0.780 0.445", "0.865 0.485");
            if (pvp)
            {
                double deaths = Stat(viewer, "Deaths");
                double kd = Stat(viewer, "KDR");
                if (kd <= 0 && deaths > 0) kd = personalScore / deaths;
                AddText(c, Root, "\u0423\u0431\u0438\u0439\u0441\u0442\u0432\u0430: " + F(personalScore), 10, "0.94 0.94 0.94 1",
                    TextAnchor.MiddleLeft, "0.878 0.545", "0.960 0.585");
                AddText(c, Root, "\u0421\u043c\u0435\u0440\u0442\u0438: " + F(deaths), 10, "0.94 0.94 0.94 1",
                    TextAnchor.MiddleLeft, "0.878 0.505", "0.960 0.545");
                AddText(c, Root, "K/D: " + kd.ToString("0.00"), 10, "0.94 0.94 0.94 1",
                    TextAnchor.MiddleLeft, "0.878 0.465", "0.960 0.505");
            }
            else
            {
                AddText(c, Root, (selected == "TimePlayed" ? "\u0412\u0440\u0435\u043c\u044f: " + TimeText(personalScore) : "\u0420\u0435\u0437\u0443\u043b\u044c\u0442\u0430\u0442: " + F(personalScore)),
                    10, "0.94 0.94 0.94 1", TextAnchor.MiddleLeft,
                    "0.878 0.500", "0.960 0.555");
            }
            // Prize panel: framed dark card with three distinct crate rewards.
            AddSolidPanel(c, Root + ".TopAwardsBorder", "0.34 0.35 0.37 0.96",
                "0.695 0.205", "0.970 0.407");
            AddSolidPanel(c, Root + ".TopAwardsPanel", "0.055 0.060 0.068 0.98",
                "0.697 0.207", "0.968 0.405");
            AddTextBold(c, Root, "🏆  НАГРАДЫ ТОПА", 15, "1 0.55 0.08 1",
                TextAnchor.MiddleLeft, "0.710 0.365", "0.950 0.400");
            AddText(c, Root, "Лучшие игроки получают награды", 10,
                "0.86 0.86 0.86 1", TextAnchor.MiddleLeft, "0.710 0.337", "0.950 0.365");
            AddText(c, Root, "по итогам вайпа", 10,
                "0.86 0.86 0.86 1", TextAnchor.MiddleLeft, "0.710 0.312", "0.950 0.340");

            // Separate medal and crate layers, evenly distributed in the rewards card.
            AddImageFixedSize(c, Root, Root + ".PrizeMedalGold", "top_medal_gold", 0.745f, 0.303f, 28f, 28f);
            AddImageFixedSize(c, Root, Root + ".PrizeCrateGold", "top_crate_gold", 0.745f, 0.250f, 66f, 66f);
            AddTextBold(c, Root, "1 \u041c\u0415\u0421\u0422\u041e", 8, "1 0.75 0.25 1",
                TextAnchor.MiddleCenter, "0.715 0.207", "0.775 0.231");

            AddImageFixedSize(c, Root, Root + ".PrizeMedalSilver", "top_medal_silver", 0.832f, 0.303f, 28f, 28f);
            AddImageFixedSize(c, Root, Root + ".PrizeCratePurple", "top_crate_purple", 0.832f, 0.250f, 66f, 66f);
            AddTextBold(c, Root, "2 \u041c\u0415\u0421\u0422\u041e", 8, "0.90 0.90 0.95 1",
                TextAnchor.MiddleCenter, "0.802 0.207", "0.862 0.231");

            AddImageFixedSize(c, Root, Root + ".PrizeMedalBronze", "top_medal_bronze", 0.919f, 0.303f, 28f, 28f);
            AddImageFixedSize(c, Root, Root + ".PrizeCrateGreen", "top_crate_green", 0.919f, 0.250f, 66f, 66f);
            AddTextBold(c, Root, "3 \u041c\u0415\u0421\u0422\u041e", 8, "0.75 0.95 0.55 1",
                TextAnchor.MiddleCenter, "0.889 0.207", "0.949 0.231");
            // Weekly wipe countdown, calculated against Thursday 17:00 Moscow time.
            AddSolidPanel(c, Root + ".WipeBorder", "0.95 0.34 0.02 0.98",
                "0.695 0.075", "0.970 0.195");
            AddSolidPanel(c, Root + ".WipePanel", "0.075 0.060 0.050 0.99",
                "0.697 0.077", "0.968 0.193");
            AddImageFixedSize(c, Root, Root + ".WipeIcon", "top_icon_time", 0.735f, 0.165f, 27f, 27f);
            AddTextBold(c, Root, "ДО КОНЦА ВАЙПА:", 13, "1 0.55 0.08 1",
                TextAnchor.MiddleLeft, "0.760 0.145", "0.950 0.185");
            AddTextBold(c, Root, GetWipeCountdown(), 23, "1 0.43 0.04 1",
                TextAnchor.MiddleCenter, "0.710 0.090", "0.955 0.151");
        }

        private void AddTopAvatar(CuiElementContainer c, ulong userId, int index, float yBottom, float yTop, float centerX = 0.312f, float size = 14f)
        {
            if (ImageLibrary == null || userId == 0) return;
            string png = null;
            try { png = ImageLibrary.Call<string>("GetImage", userId.ToString(), 1UL); }
            catch (Exception ex) { PrintWarning("Не удалось получить аватар " + userId + ": " + ex.Message); }
            if (string.IsNullOrEmpty(png) || png == "0") return;

            c.Add(new CuiElement
            {
                Name = Root + ".TopAvatar." + index,
                Parent = Root,
                Components =
                {
                    new CuiRawImageComponent { Png = png, Color = "1 1 1 1" },
                    new CuiRectTransformComponent
                    {
                        AnchorMin = XY(centerX, (yBottom + yTop) / 2f),
                        AnchorMax = XY(centerX, (yBottom + yTop) / 2f),
                        OffsetMin = XY(-size, -size),
                        OffsetMax = XY(size, size)
                    }
                }
            });
        }

        private double GetPlayerCategoryScore(BasePlayer player, string category)
        {
            if (player == null || PlayerRanks == null) return 0;
            try
            {
                object raw = PlayerRanks.Call("GetTop", category, 1, new List<ulong> { player.userID });
                var result = raw as Dictionary<ulong, Dictionary<string, object>>;
                Dictionary<string, object> row;
                object value;
                if (result != null && result.TryGetValue(player.userID, out row) &&
                    row != null && row.TryGetValue(category, out value))
                    return ToNumber(value);
            }
            catch (Exception ex)
            {
                PrintWarning("Не удалось получить личный результат " + category + ": " + ex.Message);
            }
            return 0;
        }

        private void DrawTeam(CuiElementContainer c, BasePlayer p)
        {
            AddText(c, Root, "КОМАНДА DUO", 24, "1 1 1 1", TextAnchor.MiddleLeft, "0.245 0.550", "0.600 0.600");
            if (p.currentTeam == 0)
            {
                AddText(c, Root, "Ты пока не состоишь в команде Rust.", 16, "0.9 0.9 0.9 1", TextAnchor.MiddleCenter, "0.260 0.440", "0.890 0.520");
                return;
            }

            var team = RelationshipManager.ServerInstance.FindTeam(p.currentTeam);
            if (team == null) return;

            int i = 0;
            foreach (ulong id in team.members.Take(2))
            {
                BasePlayer member = BasePlayer.FindByID(id) ?? BasePlayer.FindSleeping(id);
                string name = member == null ? id.ToString() : member.displayName;
                float top = 0.55f - i * 0.09f;
                AddText(c, Root, (id == p.userID ? "●  " : "○  ") + name + "   [" + id + "]", 15, "1 1 1 1", TextAnchor.MiddleLeft, XY(0.260f, top - 0.06f), XY(0.890f, top));
                i++;
            }
        }

        private void DrawFeaturePage(CuiElementContainer c, string tab)
        {
            string title = tab == "events" ? "СОБЫТИЯ" : tab == "awards" ? "НАГРАДЫ" : tab == "cases" ? "КЕЙСЫ" : "ДОСТИЖЕНИЯ";
            AddText(c, Root, title, 24, "1 1 1 1", TextAnchor.MiddleLeft, "0.245 0.550", "0.600 0.600");

            string[] rows = tab == "events"
                ? new[] { "BRADLEY HUNT — статистика уничтожения Bradley", "HELI HUNT — статистика уничтожения патрульного вертолёта", "AIRDROP — участие в борьбе за припасы", "WIPE RACE — активность в течение вайпа" }
                : tab == "awards"
                    ? new[] { "Лидер PvP — награда лучшему по убийствам", "Охотник — награда за убийства NPC", "Активный игрок — награда за время в игре", "Строитель — награда за строительство" }
                    : tab == "cases"
                        ? new[] { "Раздел кейсов подготовлен для следующего этапа разработки.", "Содержимое, стоимость и вероятности наград будут настроены отдельно." }
                        : new[] { "Воин — достижения за PvP", "Охотник — достижения за PvE", "Строитель — достижения за постройки", "Ветеран — достижения за время в игре" };

            for (int i = 0; i < rows.Length; i++)
            {
                float top = 0.565f - i * 0.075f;
                AddText(c, Root, rows[i], 14, "0.93 0.93 0.93 1", TextAnchor.MiddleLeft, XY(0.260f, top - 0.055f), XY(0.890f, top));
            }

            AddText(c, Root, "Для начисления сезонных наград потребуется отдельная настройка правил и призов.", 10, "0.7 0.72 0.75 1", TextAnchor.MiddleCenter, "0.260 0.330", "0.890 0.380");
        }

        private void DrawShop(CuiElementContainer c)
        {
            AddText(c, Root, "МАГАЗИН TWINX", 24, "1 1 1 1", TextAnchor.MiddleLeft, "0.245 0.550", "0.600 0.600");
            AddText(c, Root, "Магазин сервера", 20, "1 0.5 0.03 1", TextAnchor.MiddleCenter, "0.300 0.470", "0.850 0.530");
            AddText(c, Root, "Ссылка на магазин настраивается в конфигурации плагина.", 13, "0.9 0.9 0.9 1", TextAnchor.MiddleCenter, "0.300 0.410", "0.850 0.470");
        }

        private StatsSnapshot CaptureSnapshot(BasePlayer p)
        {
            return new StatsSnapshot
            {
                Kills = Stat(p, "PVPKills"),
                NpcKills = Stat(p, "NPCKills"),
                AnimalKills = SavedValue(statsData.AnimalKills, p.userID),
                HeliKills = Stat(p, "HeliKills"),
                APCKills = Stat(p, "APCKills"),
                Shots = Stat(p, "BulletsFired") + Stat(p, "ArrowsFired"),
                Built = Stat(p, "StructuresBuilt"),
                Upgraded = Stat(p, "StructuresUpgraded"),
                Demolished = Stat(p, "StructuresDemolished"),
                Crafted = Stat(p, "ItemsCrafted"),
                Deployed = Stat(p, "ItemsDeployed"),
                PlantsPlanted = SavedValue(statsData.PlantsPlanted, p.userID),
                PlantsGathered = Stat(p, "PlantsGathered"),
                Playtime = Playtime(p)
            };
        }

        private const int CurrentXpVersion = 3;

        private ProgressRecord SyncProgress(BasePlayer p)
        {
            if (p == null) return null;

            // Never overwrite a valid XP baseline with zeroes while a required
            // statistics provider is unloaded or unavailable.
            if (PlayerRanks == null || PlaytimeTracker == null)            {
                ProgressRecord saved;
                return progressData.Players.TryGetValue(p.userID, out saved) ? saved : null;
            }

            StatsSnapshot current = CaptureSnapshot(p);
            ProgressRecord record;
            if (!progressData.Players.TryGetValue(p.userID, out record) || record == null)
            {
                record = new ProgressRecord();
                record.TotalXp = CalculateXpFromSnapshot(current);
                record.Last = current;
                record.XpVersion = CurrentXpVersion;
                progressData.Players[p.userID] = record;
                SaveProgressData();
                GrantLevelRewards(p, record);
                return record;
            }

            if (record.Last == null)
                record.Last = current;

            // Version 3 is a one-time normalization. Earlier versions accumulated
            // XP with different coefficients, so their stored TotalXp could exceed
            // the XP represented by the player's actual lifetime statistics.
            // Rebuild XP from the current canonical snapshot and preserve the
            // reward checkpoint so already-paid Coin is never issued twice.
            if (record.XpVersion < CurrentXpVersion)
            {
                record.TotalXp = CalculateXpFromSnapshot(current);
                record.Last = current;
                record.XpVersion = CurrentXpVersion;
                SaveProgressData();
                GrantLevelRewards(p, record);
                Puts("XP migration v3: " + p.UserIDString + " -> " +
                    record.TotalXp.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) +
                    " XP; LastRewardedLevel=" + record.LastRewardedLevel);
                return record;
            }

            int levelBeforeGain;
            double xpBeforeGain;
            double nextBeforeGain;
            CalculateLevel(record.TotalXp, out levelBeforeGain, out xpBeforeGain, out nextBeforeGain);

            double gained = Delta(current.Kills, record.Last.Kills) * 20
                + Delta(current.NpcKills, record.Last.NpcKills) * 10
                + Delta(current.AnimalKills, record.Last.AnimalKills) * 5
                + Delta(current.HeliKills, record.Last.HeliKills) * 150
                + Delta(current.APCKills, record.Last.APCKills) * 100
                + Delta(current.Shots, record.Last.Shots) * 1000
                + Delta(current.Built, record.Last.Built)
                + Delta(current.Upgraded, record.Last.Upgraded) * 2
                + Delta(current.Demolished, record.Last.Demolished)
                + Delta(current.Crafted, record.Last.Crafted)
                + Delta(current.Deployed, record.Last.Deployed)
                + Delta(current.PlantsPlanted, record.Last.PlantsPlanted) * 2
                + Delta(current.PlantsGathered, record.Last.PlantsGathered) * 0.5
                + Delta(current.Playtime, record.Last.Playtime) / 3600.0 * 10;

            if (gained > 0 && !double.IsNaN(gained) && !double.IsInfinity(gained))
            {
                record.TotalXp += gained;

                int levelAfterGain;
                double xpAfterGain;
                double nextAfterGain;
                CalculateLevel(record.TotalXp, out levelAfterGain, out xpAfterGain, out nextAfterGain);

                // Play one level-up sound for every level crossed in this sync.
                for (int crossedLevel = levelBeforeGain + 1; crossedLevel <= levelAfterGain; crossedLevel++)
                    PlayLevelUpSound(p);
            }

            record.Last = current;
            SaveProgressData();
            GrantLevelRewards(p, record);
            return record;
        }

        private void PlayLevelUpSound(BasePlayer player)
        {
            if (player == null || !player.IsConnected) return;

            try
            {
                Effect.server.Run(
                    "assets/prefabs/misc/halloween/lootbag/effects/gold_open.prefab",
                    player.transform.position);
            }
            catch (Exception ex)
            {
                PrintWarning("Не удалось воспроизвести звук повышения уровня: " + ex.Message);
            }
        }

        private int CoinRewardForLevel(int level)
        {
            if (level >= 2 && level <= 10) return 10;
            if (level >= 11 && level <= 20) return 14;
            if (level >= 21 && level <= 30) return 20;
            if (level >= 31 && level <= 50) return 24;
            if (level >= 51 && level <= 75) return 30;
            if (level >= 76 && level <= 100) return 40;
            return 0;
        }

        private void GrantLevelRewards(BasePlayer player, ProgressRecord record)
        {
            if (player == null || record == null) return;

            int currentLevel;
            double currentXp;
            double nextXp;
            CalculateLevel(record.TotalXp, out currentLevel, out currentXp, out nextXp);

            // Level 1 is the starting level and has no reward.
            int lastRewarded = Math.Max(1, record.LastRewardedLevel);
            if (currentLevel <= lastRewarded)
            {
                if (record.LastRewardedLevel < 1)
                {
                    record.LastRewardedLevel = 1;
                    SaveProgressData();
                }
                return;
            }

            for (int level = lastRewarded + 1; level <= currentLevel; level++)
            {
                int reward = CoinRewardForLevel(level);
                if (reward <= 0)
                {
                    record.LastRewardedLevel = level;
                    SaveProgressData();
                    continue;
                }

                // TwinxCoin's server-console bridge is confirmed to work on this server.
                // Its claimId makes retries safe and prevents duplicate Coin payments.
                try
                {
                    string command = "twinxcoin.reward " + player.userID + " " +
                        reward + " " + level + " level_" + level;
                    ConsoleSystem.Run(ConsoleSystem.Option.Server, command);
                }
                catch (Exception ex)
                {
                    PrintWarning("Не удалось вызвать команду TwinxCoin для уровня " + level +
                        ": " + ex.Message);
                    break;
                }

                bool claimConfirmed = false;
                try
                {
                    CoinDataMirror coinData = Interface.Oxide.DataFileSystem.ReadObject<CoinDataMirror>("TwinxCoin_Data");
                    string claimKey = player.userID.ToString() + ":level_" + level;
                    claimConfirmed = coinData != null && coinData.RewardClaims != null &&
                        coinData.RewardClaims.ContainsKey(claimKey);
                }
                catch (Exception ex)
                {
                    PrintWarning("Не удалось проверить запись награды TwinxCoin: " + ex.Message);
                }

                if (!claimConfirmed)
                {
                    PrintWarning("TwinxCoin не подтвердил запись награды за уровень " +
                        level + ". Игрок: " + player.UserIDString);
                    break;
                }

                record.LastRewardedLevel = level;
                SaveProgressData();
                SendReply(player, "<color=#ff8a32>TWINX</color>: получено <color=#ffd18a>" +
                    reward + " Coin</color> за достижение " + level + " уровня.");
                Puts("Level reward confirmed: " + player.UserIDString +
                    " | level " + level + " | " + reward + " Coin");
            }
        }

        private double CalculateXpFromSnapshot(StatsSnapshot value)
        {
            if (value == null) return 0;

            // Canonical lifetime XP formula. Keep this identical to the delta
            // coefficients in SyncProgress so migration and future gains match.
            double total =
                Math.Max(0, value.Kills) * 20
                + Math.Max(0, value.NpcKills) * 10
                + Math.Max(0, value.AnimalKills) * 5
                + Math.Max(0, value.HeliKills) * 150
                + Math.Max(0, value.APCKills) * 100
                + Math.Max(0, value.Shots) * 1000
                + Math.Max(0, value.Built)
                + Math.Max(0, value.Upgraded) * 2
                + Math.Max(0, value.Demolished)
                + Math.Max(0, value.Crafted)
                + Math.Max(0, value.Deployed)
                + Math.Max(0, value.PlantsPlanted) * 2
                + Math.Max(0, value.PlantsGathered) * 0.5
                + Math.Max(0, value.Playtime) / 3600.0 * 10;

            return double.IsNaN(total) || double.IsInfinity(total) ? 0 : total;
        }

        private double Delta(double current, double previous)
        {
            // Never subtract XP when a statistic is reset by an administrator or wipe.
            return Math.Max(0, current - previous);
        }

        private double Stat(BasePlayer p, string category)
        {
            if (PlayerRanks == null || p == null) return 0;
            try
            {
                object raw = PlayerRanks.Call("GetTop", category, 1, new List<ulong> { p.userID });
                var top = raw as Dictionary<ulong, Dictionary<string, object>>;
                if (top == null) return 0;
                Dictionary<string, object> row;
                if (!top.TryGetValue(p.userID, out row) || row == null) return 0;
                object rawValue;
                if (!row.TryGetValue(category, out rawValue) || rawValue == null) return 0;
                double value;
                return double.TryParse(Convert.ToString(rawValue), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out value) ? value : 0;
            }
            catch (Exception ex)
            {
                PrintWarning("Не удалось получить личную статистику " + category + ": " + ex.Message);
                return 0;
            }
        }

        // PlaytimeTracker is the single source of truth for every live playtime value.
        private double Playtime(BasePlayer p)
        {
            if (PlaytimeTracker == null || p == null) return 0;
            try
            {
                object raw = PlaytimeTracker.Call("GetPlayTime", p.UserIDString);
                double value;
                return raw != null && double.TryParse(Convert.ToString(raw), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out value) ? value : 0;
            }
            catch (Exception ex)
            {
                PrintWarning("Не удалось получить время игры из PlaytimeTracker: " + ex.Message);
                return 0;
            }
        }

        private void CalculateLevel(double totalXp, out int level, out double currentXp, out double nextXp)
        {
            level = 1;
            currentXp = double.IsNaN(totalXp) || double.IsInfinity(totalXp)
                ? 0 : Math.Max(0, totalXp);
            nextXp = 500;

            while (level < 100 && currentXp >= nextXp)
            {
                currentXp -= nextXp;
                level++;
                nextXp = level * 500.0;
            }

            if (level >= 100)
            {
                level = 100;
                currentXp = nextXp;
            }
        }

        private void AddPlayerAvatar(CuiElementContainer c, BasePlayer player)
        {
            if (ImageLibrary == null || player == null) return;

            string png = null;
            try
            {
                // ImageLibrary stores each player's full-size Steam avatar under image ID 1.
                png = ImageLibrary.Call<string>("GetImage", player.UserIDString, 1UL);
            }
            catch (Exception ex)
            {
                PrintWarning("Не удалось получить Steam-аватар игрока " + player.UserIDString + ": " + ex.Message);
            }

            if (string.IsNullOrEmpty(png) || png == "0") return;

            c.Add(new CuiElement
            {
                Name = Root + ".SteamAvatar",
                Parent = Root,
                Components =
                {
                    new CuiRawImageComponent { Png = png, Color = "1 1 1 1" },
                    new CuiRectTransformComponent
                    {
                        AnchorMin = "0.2975 0.711",
                        AnchorMax = "0.2975 0.711",
                        OffsetMin = "-47 -47",
                        OffsetMax = "47 47"
                    }
                }
            });
        }

        private void AddImage(CuiElementContainer c, string parent, string name, string key, string min, string max)
        {
            string png = null;
            if (ImageLibrary != null)
            {
                try { png = ImageLibrary.Call<string>("GetImage", "twinx_" + key); }
                catch { }
            }

            if (string.IsNullOrEmpty(png)) return;

            c.Add(new CuiElement
            {
                Name = name,
                Parent = parent,
                Components =
                {
                    new CuiRawImageComponent { Png = png, Color = "1 1 1 1" },
                    new CuiRectTransformComponent { AnchorMin = min, AnchorMax = max }
                }
            });
        }

        private void AddImageFixedSize(CuiElementContainer c, string parent, string name, string key, float centerX, float centerY, float width, float height)
        {
            string png = null;

            if (ImageLibrary != null)
            {
                try { png = ImageLibrary.Call<string>("GetImage", "twinx_" + key); }
                catch { }
            }

            if (string.IsNullOrEmpty(png)) return;

            c.Add(new CuiElement
            {
                Name = name,
                Parent = parent,
                Components =
                {
                    new CuiRawImageComponent { Png = png, Color = "1 1 1 1" },
                    new CuiRectTransformComponent
                    {
                        AnchorMin = XY(centerX, centerY),
                        AnchorMax = XY(centerX, centerY),
                        OffsetMin = XY(-width / 2f, -height / 2f),
                        OffsetMax = XY(width / 2f, height / 2f)
                    }
                }
            });
        }

        private void AddTextOnPanelBold(CuiElementContainer c, string parent, string text, int size, string color, TextAnchor align, float centerX, float centerY, float left, float bottom, float right, float top)
        {
            c.Add(new CuiLabel
            {
                Text = { Text = text, Font = "robotocondensed-bold.ttf", FontSize = size, Align = align, Color = color },
                RectTransform =
                {
                    AnchorMin = XY(centerX, centerY),
                    AnchorMax = XY(centerX, centerY),
                    OffsetMin = XY(left, bottom),
                    OffsetMax = XY(right, top)
                }
            }, parent);
        }

        private void AddTextOnPanel(CuiElementContainer c, string parent, string text, int size, string color, TextAnchor align, float centerX, float centerY, float left, float bottom, float right, float top)
        {
            c.Add(new CuiLabel
            {
                Text = { Text = text, Font = "robotocondensed-regular.ttf", FontSize = size, Align = align, Color = color },
                RectTransform =
                {
                    AnchorMin = XY(centerX, centerY),
                    AnchorMax = XY(centerX, centerY),
                    OffsetMin = XY(left, bottom),
                    OffsetMax = XY(right, top)
                }
            }, parent);
        }

        private void AddTextBold(CuiElementContainer c, string parent, string text, int size, string color, TextAnchor align, string min, string max)
        {
            c.Add(new CuiLabel
            {
                Text = { Text = text, Font = "robotocondensed-bold.ttf", FontSize = size, Align = align, Color = color },
                RectTransform = { AnchorMin = min, AnchorMax = max }
            }, parent);
        }

        private void AddText(CuiElementContainer c, string parent, string text, int size, string color, TextAnchor align, string min, string max)
        {
            c.Add(new CuiLabel
            {
                Text = { Text = text, Font = "robotocondensed-regular.ttf", FontSize = size, Align = align, Color = color },
                RectTransform = { AnchorMin = min, AnchorMax = max }
            }, parent);
        }

        private void AddButton(CuiElementContainer c, string parent, string name, string text, string command, string min, string max)
        {
            c.Add(new CuiButton
            {
                Button = { Color = "0 0 0 0", Command = command, Close = "" },
                RectTransform = { AnchorMin = min, AnchorMax = max },
                Text = { Text = text, FontSize = 1, Color = "0 0 0 0" }
            }, parent, name);
        }

        private string TimeText(double seconds)
        {
            if (seconds < 0) seconds = 0;
            TimeSpan t = TimeSpan.FromSeconds(seconds);
            return ((int)t.TotalHours) + " ч " + t.Minutes + " м";
        }

        private string F(double value)
        {
            return value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
        }

        private string XY(float x, float y)
        {
            return x.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + " " + y.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}




















