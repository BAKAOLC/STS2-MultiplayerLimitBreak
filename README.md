# Multiplayer Limit Break

一个 Slay the Spire 2 多人模式限制突破 Mod。

## 用途

将多人模式人数上限从原版 4 人提高到最多 16 人，并配套调整大厅传输、房间布局和多人难度缩放。

0.2.0 起不再提供启用开关，也不再修改原版消息的字段位宽。模组会通过 RitsuLib 在原版大厅消息末尾携带有版本和边界校验的扩展数据，并在 1–4 人时持续记录所有玩家的协议能力。房间需要加入第 5 名玩家时，只有当前玩家和加入者均支持兼容协议才会自动扩容；否则拒绝本次扩容，并向能接收原因的房主和加入者显示提示。原版客户端仍可进入不超过 4 人的房间，但其留在房间期间无法安全扩容。

模组始终从原版联机 mod 匹配列表中排除，通过独立的能力握手决定是否允许扩容。额外人数倍率在联机时自动跟随房主设置。

## 依赖

- [STS2-RitsuLib](https://github.com/BAKAOLC/sts-2-ritsulib) 0.5.3 或更高版本

## Reference

本项目的基本思路来自 [Rain156/sts2-RMP-Mods](https://github.com/Rain156/sts2-RMP-Mods)。

## English

A Slay the Spire 2 mod that raises the multiplayer player limit from the vanilla 4 players to up to 16 players, with related lobby transport, room layout, and multiplayer difficulty-scaling adjustments.

Since 0.2.0, there is no enable switch and vanilla message field widths are left unchanged. RitsuLib appends versioned, bounds-checked extension data to the original lobby messages, and every peer's capability is tracked even while the room has 1–4 players. Expansion is activated automatically when player 5 joins, but only if every existing player and the joining player support a compatible protocol. Otherwise the expansion attempt is rejected and both the host and a capable joining client receive a reason. Vanilla clients may still join rooms of up to 4 players, but the room cannot safely expand while one remains.

The mod is always removed from vanilla multiplayer mod matching and uses its own capability handshake for expansion admission. The extra-player scaling multiplier follows the host during multiplayer.

Requires [STS2-RitsuLib](https://github.com/BAKAOLC/sts-2-ritsulib) 0.5.3 or later.

The basic idea for this project comes from [Rain156/sts2-RMP-Mods](https://github.com/Rain156/sts2-RMP-Mods).
