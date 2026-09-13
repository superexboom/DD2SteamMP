$ErrorActionPreference = 'Stop'

$runnerPath = Join-Path $PSScriptRoot 'DD2SteamMultiplayerHost\DD2SteamMultiplayerRunner.cs'
$runner = Get-Content -LiteralPath $runnerPath -Raw -Encoding UTF8
$rngPath = Join-Path $PSScriptRoot 'DD2SteamMultiplayerHost\ArenaRandomContext.cs'
$rng = Get-Content -LiteralPath $rngPath -Raw -Encoding UTF8
$mcpPath = Join-Path $PSScriptRoot 'automation\mcp_server.py'
$mcp = Get-Content -LiteralPath $mcpPath -Raw -Encoding UTF8
$required = @(
    'string.Equals(definition.m_Id, "ambush_monsters", StringComparison.Ordinal)',
    'private bool TryResolveHeroTurnOwner(',
    '_coopHeroControlSlots.TryGetValue(info.ActorGuid, out controlSlot)',
    'if (controlSlot > 0 && controlSlot <= 4)',
    '_coopHeroControlSlots[actor.ActorGuid] = controlSlot;',
    'slots == _arenaHeroDraftSlots',
    'IsHeroVsHeroControlBindingExcluded()',
    'Matrix4x4.Scale(new Vector3(setupScale, setupScale, 1f))',
    '_hoverTooltipScreenPosition *= setupScale;',
    'UiInputBlocker.RegisterRect(_arenaHeroSetupRect, setupScale)',
    'DrawArenaHeroDetailTabFixedHeader(slot);',
    'DrawArenaHeroDetailTabCandidates(slot);',
    'GUILayout.ExpandHeight(true));',
    'run_test_boss_modifier',
    'ClearArenaBossModifierEditorPref()',
    'Normalizing launch mode',
    '_arenaRandomContext.TryBegin',
    '_arenaRandomContext?.End',
    'ArenaBossModifierPrefix',
    'ArenaBossModifierRollPrefix',
    'IsArenaEnemyActorClass',
    'bossModifierOriginal',
    'arena_status',
    'arena_start',
    'arena_cancel',
    'arena_rng_test',
    'combatdetail'
)
foreach ($text in $required) {
    if (-not ($runner + $rng + $mcp).Contains($text)) { throw "Missing MP contract: $text" }
}

foreach ($text in @(
    'RandomContainer.SaveToJson()',
    'RandomContainer.LoadFromJson(_savedJson)',
    'RandomContainer.SetSeed(identifier',
    'RandomIdentifier.ACTOR_CONTROLLER',
    'RandomIdentifier.BOSS',
    'RandomNumberGenerator.Create()',
    'ReturnToUnidentifiedState',
    'm_initialRandomMap'
)) {
    if (-not $rng.Contains($text)) { throw "Missing Arena RNG contract: $text" }
}

if ($runner.Contains('float pick = UnityEngine.Random.Range(0f, totalWeight)')) {
    throw 'Arena battle advantage still consumes UnityEngine.Random directly.'
}

if (-not ($mcp.Contains('tools/call') -and $mcp.Contains('command.txt') -and $mcp.Contains('doorstop_host.log'))) {
    throw 'Arena MCP wrapper is incomplete.'
}

if ($runner.Contains('GetArenaHeroCandidateScrollHeight') -or
    $runner.Contains('GUILayout.Height(candidateHeight)')) {
    throw 'Hero setup still uses a hard-coded candidate height.'
}

$resolverStart = $runner.IndexOf('private bool TryResolveHeroTurnOwner(')
$resolverEnd = $runner.IndexOf('private void DrawArenaPanelSection()', $resolverStart)
$resolver = $runner.Substring($resolverStart, $resolverEnd - $resolverStart)
if ($resolver.IndexOf('_coopHeroControlSlots.TryGetValue(info.ActorGuid, out controlSlot)') -lt 0 -or
    $resolver.IndexOf('_session.TryGetHeroSlotOwner(controlSlot, out owner)') -lt 0) {
    throw 'Hero identity must resolve before the selected control-slot owner.'
}
if ($resolver.Contains('info.TeamPosition + 1')) {
    throw 'Bound hero control must not fall back to current rank.'
}
if (-not $resolver.Contains('if (_bindCoopControlsToHeroes && !IsHeroVsHeroControlBindingExcluded())')) {
    throw 'PVP must retain its existing turn-owner route.'
}

$scale1600x900 = [Math]::Max(0.80, [Math]::Min(1.0, [Math]::Min((1600 - 16) / 1520.0, (900 - 16) / 1040.0)))
if ([Math]::Abs($scale1600x900 - 0.85) -gt 0.001) {
    throw "Unexpected 1600x900 hero-setup scale: $scale1600x900"
}
if (1040 * $scale1600x900 -gt 884.01 -or 820 * $scale1600x900 -lt 696.99) {
    throw 'Hero-setup scale violates the fit/min-height contract.'
}

Write-Host 'MP PVE control/layout contract: PASS'
