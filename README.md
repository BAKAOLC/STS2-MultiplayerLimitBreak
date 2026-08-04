# Multiplayer Limit Break

一个 Slay the Spire 2 多人模式限制突破 Mod。

## 用途

将多人模式人数上限从原版 4 人提高到最多 16 人，并配套调整大厅传输、房间布局和多人难度缩放。

0.2.0 起不再提供启用开关，也不再修改原版消息的字段位宽。模组会通过 RitsuLib 在原版大厅消息末尾携带有版本和边界校验的扩展数据，并在 1–4 人时持续记录所有玩家的协议能力。房间需要加入第 5 名玩家时，只有当前玩家和加入者均支持兼容协议才会自动扩容；否则拒绝本次扩容，并向能接收原因的房主和加入者显示提示。原版或旧版客户端仅在当前人数少于 4 人且所有现有玩家均位于原版 0–3 槽位时允许加入；房间曾经扩容后，如果仍有高槽位玩家存留，会拒绝无法恢复完整列表的客户端，直至高槽位清空后自动恢复准入。其留在房间期间无法再次扩容。

模组始终从原版联机 mod 匹配列表中排除，通过独立的能力握手决定是否允许扩容。额外人数倍率在联机时自动跟随房主设置。

同一个 DLL 通过运行时 API 适配同时支持游戏 0.107.1 的 `LobbyPlayer` 和新版本的 `StartRunLobbyPlayer`，无需按游戏版本替换模组 DLL。

大厅玩家列表上方会常驻显示“【多人上限解限】已激活”或“因不兼容客户端已禁用”，并附带当前人数和对应上限。进入 5 人扩容状态后文本保持不变，仅将状态颜色由绿色变为蓝色。相关玩家名称旁会显示小型琥珀色警告三角，不会覆盖原版头像上的准备或断线状态。

## 依赖

- [STS2-RitsuLib](https://github.com/BAKAOLC/sts-2-ritsulib) 0.5.4 或更高版本

## Reference

本项目的基本思路来自 [Rain156/sts2-RMP-Mods](https://github.com/Rain156/sts2-RMP-Mods)。

## English

A Slay the Spire 2 mod that raises the multiplayer player limit from the vanilla 4 players to up to 16 players, with related lobby transport, room layout, and multiplayer difficulty-scaling adjustments.

Since 0.2.0, there is no enable switch and vanilla message field widths are left unchanged. RitsuLib appends versioned, bounds-checked extension data to the original lobby messages, and every peer's capability is tracked even while the room has 1–4 players. Expansion is activated automatically when player 5 joins, but only if every existing player and the joining player support a compatible protocol. Otherwise the expansion attempt is rejected and both the host and a capable joining client receive a reason. Vanilla or old clients may join only while the current player count is below 4 and every existing player occupies a vanilla slot from 0 through 3. After a room contracts from an expanded state, clients that cannot restore the full roster are rejected while any high-slot survivor remains; admission is restored automatically after those slots are clear. Expansion remains unavailable while such a client stays.

The mod is always removed from vanilla multiplayer mod matching and uses its own capability handshake for expansion admission. The extra-player scaling multiplier follows the host during multiplayer.

The same DLL supports both the `LobbyPlayer` API used by game 0.107.1 and the `StartRunLobbyPlayer` API used by newer versions through runtime adaptation; no version-specific mod DLL is required.

A persistent Multiplayer Limit Break indicator above the lobby player list reports Active or Disabled by incompatible clients together with the current player count and applicable limit. After five-player expansion activates, the text stays unchanged and the indicator changes from green to blue. Affected players receive a small amber warning triangle beside the nameplate without touching the vanilla ready or disconnected indicators.

Requires [STS2-RitsuLib](https://github.com/BAKAOLC/sts-2-ritsulib) 0.5.4 or later.

The basic idea for this project comes from [Rain156/sts2-RMP-Mods](https://github.com/Rain156/sts2-RMP-Mods).
