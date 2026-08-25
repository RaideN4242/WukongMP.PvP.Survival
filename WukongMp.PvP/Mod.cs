using CSharpModBase;
using CSharpModBase.Input;
using Microsoft.Extensions.Logging;
using ReadyM.Api.DI;
using ReadyM.Api.Idents;
using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using UnrealEngine.Runtime;
using WukongMp.Api;
using WukongMp.Api.Configuration;
using WukongMp.Api.UI;
using WukongMp.PvP.Chat;
using WukongMp.PvP.Command;
using WukongMp.PvP.Configuration;
using WukongMp.PvP.GameMode;
using WukongMp.PvP.UI;
using WukongMp.Sdk;
using WukongMp.Sdk.Api;
using WukongMp.Sdk.Entities;

namespace WukongMp.PvP;

// ReSharper disable once UnusedType.Global
public class Mod : ModBase
{
    public override string Name => "WukongMp PvP";

    private WaveManager? _waveManager;
    private GameMessageWidget? _waveWidget;

    protected override void Initialize(IDependencyContainer services)
    {
        Logger.LogInformation("Initializing {PluginName}", Name);

        services.RegisterSingleton<PvpRpc>();
        services.RegisterSingleton<TimerController>();
        services.RegisterSingleton<PvpChatter>();
        services.RegisterSingleton<PvpGameplayConfiguration>();
        services.RegisterSingleton<PvpSaveManager>();
        services.RegisterSingleton<PvpWidgetManager>();
        services.RegisterSingleton<PvpMode>();
        services.RegisterSingleton<PvpCommandHandler>();
        services.RegisterSingleton<PvpSynchronizer>();

        WukongApi.Events.OnLoadingScreenClose += OnLoadingScreenClose;
        WukongApi.Events.OnLevelLoaded += OnLevelLoaded;
    }

    private void OnLevelLoaded()
    {
        _waveWidget = new GameMessageWidget();
        _waveWidget.Initialize();
        _waveWidget.SetVisibility(false);
    }

    private void OnLoadingScreenClose()
    {
        if (WukongApi.Local.IsGameplayLevel)
        {
            WukongApi.Chat.ShowLocalMessage("Welcome to Sirvival Mod!", FLinearColor.Cyan);

            WukongApi.Configuration.IsStrongDamageImmueEnabled = false;
            WukongApi.Configuration.ClearDisableTamerAttackQuery();

            if (_waveWidget != null)
            {
                _waveWidget.SetVisibility(true);
                _waveWidget.SetMainText("Killing Floor");
                _waveWidget.SetSecondText("Wave 1");
            }

            _waveManager ??= new WaveManager(_waveWidget);
            _waveManager.StartWave();
        }
    }

    public override void LateInit()
    {
        base.LateInit();

        WukongApi.Input.RegisterKeyBind(Key.J, () =>
        {
            Logger.LogDebug("J");
            if (WukongApi.Input.CanApplyInput())
                WukongApi.Services.Resolve<PvpMode>().SwitchReadyStateMulti();
        });


    }

    public override void DeInit()
    {
        WukongApi.Events.OnLoadingScreenClose -= OnLoadingScreenClose;
        WukongApi.Events.OnLevelLoaded -= OnLevelLoaded;
        _waveManager?.Cleanup();
        _waveManager = null;
        _waveWidget?.Deinitialize();
        _waveWidget = null;
    }
}

public class WaveManager
{
    private bool _isWaveActive;
    private int _currentWave;
    private int _killedEnemies;
    private int _totalEnemiesInWave;
    private AreaId? _currentAreaId;
    private GameMessageWidget? _waveWidget;
    private bool _isRestarting;

    private const int MAX_WAVES = 2;
    private const float SPAWN_RADIUS = 1500f;
    private const int WAVE_DELAY_MS = 10000;
    private const int RESTART_DELAY_MS = 30000;

    private const int RED_TEAM_ID = -9999;
    private const int BLUE_TEAM_ID = -9998;

    public WaveManager(GameMessageWidget? widget = null)
    {
        _waveWidget = widget;
        WukongApi.Events.OnMonsterDead += OnMonsterDead;
    }

    public void StartWave()
    {
        if (_isWaveActive)
        {
            return;
        }

        if (!WukongApi.Local.IsGameplayLevel)
        {
            return;
        }

        var currentArea = WukongApi.Sync.CurrentAreaId;
        if (!currentArea.HasValue)
        {
            return;
        }

        _currentAreaId = currentArea.Value;
        _currentWave++;

        if (_currentWave > MAX_WAVES)
        {
            _currentWave = 1;
        }

        _isWaveActive = true;
        _killedEnemies = 0;
        _isRestarting = false;

        SetAllPlayersToBlueTeam();

        TamerKind[] waveEnemies = _currentWave switch
        {
            1 => new TamerKind[]
            {
                TamerKinds.WolfScout, TamerKinds.WolfScout, TamerKinds.WolfScout,
                TamerKinds.WolfScout, TamerKinds.WolfScout, TamerKinds.WolfScout,
                TamerKinds.WolfScout, TamerKinds.WolfScout, TamerKinds.WolfScout
            },
            2 => new TamerKind[]
            {
                TamerKinds.WolfScout, TamerKinds.WolfScout, TamerKinds.WolfScout,
                TamerKinds.WolfScout, TamerKinds.WolfScout, TamerKinds.WolfScout,
                TamerKinds.WolfScout, TamerKinds.WolfScout,
                TamerKinds.WolfScout, TamerKinds.WolfScout
            },
            _ => Array.Empty<TamerKind>()
        };

        _totalEnemiesInWave = waveEnemies.Length;

        UpdateWidget();

        WukongApi.Local.ShowInfoMessage($"Wave {_currentWave} - Prepare for battle!", 3.0f);
        WukongApi.Chat.SendServerMessage($"Wave {_currentWave} starting! Kill all enemies!");

        _ = StartSpawningEnemiesAsync(waveEnemies);
    }

    private void SetAllPlayersToBlueTeam()
    {
        var playerIds = WukongApi.Sync.AreaPlayers;

        foreach (var playerId in playerIds)
        {
            try
            {
                var character = WukongApi.Sync.GetMainCharacterByPlayerId(playerId);

                if (character.HasValue)
                {
                    ReadyCharacterExtensions.set_TeamId(character.Value, BLUE_TEAM_ID);
                }
            }
            catch (Exception ex)
            {
                WukongApi.Chat.ShowLocalMessage($"Failed to set team: {ex.Message}", FLinearColor.Red);
            }
        }
    }

    private void UpdateWidget()
    {
        if (_waveWidget != null)
        {
            _waveWidget.SetMainText($"WAVE {_currentWave}");
            _waveWidget.SetSecondText($"Enemies: {_killedEnemies}/{_totalEnemiesInWave}");
        }
    }

    private async Task StartSpawningEnemiesAsync(TamerKind[] enemies)
    {
        for (int i = 0; i < enemies.Length; i++)
        {
            if (!_isWaveActive) break;

            SpawnEnemyNearPlayer(enemies[i]);

            await Task.Delay(i == 0 ? 1000 : 3000);
        }
    }

    private void SpawnEnemyNearPlayer(TamerKind enemyType)
    {
        if (!_isWaveActive) return;

        try
        {
            if (!WukongApi.Sync.LocalMainCharacter.HasValue)
            {
                return;
            }

            var playerPos = WukongApi.Sync.LocalMainCharacter.Value.Location;
            var random = new Random();

            var angle = random.NextDouble() * Math.PI * 2;
            var distance = random.NextDouble() * SPAWN_RADIUS + 500;

            var offsetX = (float)(Math.Cos(angle) * distance);
            var offsetY = (float)(Math.Sin(angle) * distance);

            var spawnPos = new Vector3(
                playerPos.X + offsetX,
                playerPos.Y + offsetY,
                playerPos.Z
            );

            // !!! ГЛАВНОЕ ИЗМЕНЕНИЕ: оборачиваем в TryRunOnGameThread !!!
            Utils.TryRunOnGameThread(() =>
            {
                WukongApi.Sync.SpawnEnemy(
                    kind: enemyType,
                    position: spawnPos,
                    count: 1,
                    teamId: RED_TEAM_ID
                );
            });
        }
        catch (Exception ex)
        {
            WukongApi.Chat.ShowLocalMessage($"Failed to spawn enemy: {ex.Message}", FLinearColor.Red);
        }
    }

    private async void OnMonsterDead(ReadyTamer tamer, ReadyCharacter? killer)
    {
        if (!_isWaveActive) return;

        _killedEnemies++;
        UpdateWidget();

        var alivePlayers = WukongApi.Sync.AllMainCharacters.Count(x => !x.IsDead);

        if (alivePlayers == 0 && _isWaveActive && !_isRestarting)
        {
            _isWaveActive = false;
            _isRestarting = true;

            WukongApi.Chat.SendServerMessage("All players are dead! Wave failed!");
            WukongApi.Local.ShowInfoMessage("Wave Failed - All players died!", 5.0f);

            if (_waveWidget != null)
            {
                _waveWidget.SetMainText("WAVE FAILED");
                _waveWidget.SetSecondText("Respawning...");
            }

            await Task.Delay(2000);
            RespawnPlayers();

            WukongApi.Chat.SendServerMessage($"Restarting wave 1 in {RESTART_DELAY_MS / 1000} seconds...");
            await Task.Delay(RESTART_DELAY_MS);

            StartWave();
            return;
        }

        if (_killedEnemies % 2 == 0 || _killedEnemies >= _totalEnemiesInWave - 2)
        {
            var remaining = _totalEnemiesInWave - _killedEnemies;
            var message = remaining > 0
                ? $"Progress: {_killedEnemies}/{_totalEnemiesInWave} - {remaining} remaining | Players alive: {alivePlayers}"
                : "All enemies defeated!";

            WukongApi.Chat.ShowLocalMessage(message, FLinearColor.Yellow);
        }

        if (_killedEnemies >= _totalEnemiesInWave)
        {
            CompleteWave();
        }
    }

    private async void CompleteWave()
    {
        _isWaveActive = false;

        WukongApi.Local.ShowInfoMessage($"Wave {_currentWave} Completed!", 5.0f);
        WukongApi.Chat.SendServerMessage($"Wave {_currentWave} Completed! All enemies have been defeated!");

        RespawnPlayers();

        if (_currentWave >= MAX_WAVES)
        {
            _isRestarting = true;
            WukongApi.Chat.SendServerMessage($"All waves completed! Restarting in {RESTART_DELAY_MS / 1000} seconds...");

            if (_waveWidget != null)
            {
                _waveWidget.SetMainText("ALL WAVES");
                _waveWidget.SetSecondText("COMPLETED! Restarting...");
            }

            await Task.Delay(RESTART_DELAY_MS);

            if (_isRestarting)
            {
                StartWave();
            }
        }
        else
        {
            WukongApi.Chat.SendServerMessage($"Next wave in {WAVE_DELAY_MS / 1000} seconds...");

            await Task.Delay(WAVE_DELAY_MS);

            if (!_isWaveActive && !_isRestarting)
            {
                StartWave();
            }
        }
    }

    private void RespawnPlayers()
    {
        var playerIds = WukongApi.Sync.AreaPlayers;

        foreach (var playerId in playerIds)
        {
            try
            {
                var character = WukongApi.Sync.GetMainCharacterByPlayerId(playerId);

                if (character.HasValue)
                {
                    var mainChar = character.Value;

                    if (mainChar.IsSpectator)
                    {
                        WukongApi.Sync.DisableSpectatorMode(mainChar);
                    }

                    mainChar.RebirthInPlace();

                    ReadyCharacterExtensions.set_TeamId(mainChar, BLUE_TEAM_ID);
                }
            }
            catch (Exception ex)
            {
                WukongApi.Chat.ShowLocalMessage($"Failed to respawn player: {ex.Message}", FLinearColor.Red);
            }
        }

        WukongApi.Chat.SendServerMessage("All players have been revived for the next wave!");
    }

    public void Cleanup()
    {
        WukongApi.Events.OnMonsterDead -= OnMonsterDead;
        _isWaveActive = false;
        _isRestarting = false;
    }
}
