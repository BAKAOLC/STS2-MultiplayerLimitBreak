# Multiplayer Limit Break

最多 16 人联机爬塔，附带 Steam 房间邀请、匹配分组与自定义额外人数倍率。

## 用途

将多人模式人数上限从原版 4 人提高到最多 16 人，并配套调整大厅传输、房间布局和多人难度缩放。

0.2.0 起不再提供启用开关，也不再修改原版消息的字段位宽。模组会通过 RitsuLib 在原版大厅消息末尾携带有版本和边界校验的扩展数据，并在 1–4 人时持续记录所有玩家的协议能力。房间需要加入第 5 名玩家时，只有当前玩家和加入者均支持兼容协议才会自动扩容；否则拒绝本次扩容，并向能接收原因的房主和加入者显示提示。原版或旧版客户端仅在当前人数少于 4 人且所有现有玩家均位于原版 0–3 槽位时允许加入；房间曾经扩容后，如果仍有高槽位玩家存留，会拒绝无法恢复完整列表的客户端，直至高槽位清空后自动恢复准入。其留在房间期间无法再次扩容。

模组始终从原版联机 mod 匹配列表中排除，通过独立的能力握手决定是否允许扩容。额外人数倍率在联机时自动跟随房主设置。

支持游戏 0.107.1、0.111 和 0.113。

大厅玩家列表上方会常驻显示“【多人上限解限】已激活”或“因不兼容客户端已禁用”，并附带当前人数和对应上限。进入 5 人扩容状态后文本保持不变，仅将状态颜色由绿色变为蓝色。相关玩家名称旁会显示小型琥珀色警告三角，不会覆盖原版头像上的准备或断线状态。

## 房间分享与匹配

Steam 环境下，房间设置集中管理邀请、可见性和额外人数倍率。新房间默认仅好友，房主可在等待大厅改为公开；倍率修改会同步给房内玩家。

- 可复制 Steam 房间链接或 `MLB1` 邀请码。在加入界面点击刷新旁的“输入邀请码”，粘贴任一种即可通过原版流程加入。Steam 链接也支持未安装本模组的玩家，邀请码输入需要本模组。邀请在大厅销毁后失效，加入仍受房间权限、人数与兼容性限制。
- 最多保存 8 个匹配分组，每组分别设置本地名称、匹配码、是否搜索、是否在自己担任房主时发布。匹配码区分大小写、忽略首尾空格，名称无需与朋友相同。
- 只有公开的房间参与匹配分组发现；房间可以同时发布到多个分组。匹配结果使用原版房间条目，追加到原版好友列表并按房间去重。分组结果自动更新，刷新按钮同时刷新好友和匹配房间。
- 匹配码用于发现房间，不是入房密码。修改或撤下匹配码不会移除现有玩家，也不会撤销其他人已经取得的房间链接。
- 邀请码、匹配码及加入输入默认隐藏，关闭设置或切换房间后恢复隐藏。
- 非 Steam 环境不启用房间分享与发现，原有人数扩展功能保持独立。

## 依赖

- [STS2-RitsuLib](https://github.com/BAKAOLC/sts-2-ritsulib) 0.5.14 或更高版本

## Reference

本项目的基本思路来自 [Rain156/sts2-RMP-Mods](https://github.com/Rain156/sts2-RMP-Mods)。

## English

Up to 16 players per run, with Steam room invites, match groups, and adjustable extra-player scaling.

Since 0.2.0, there is no enable switch and vanilla message field widths are left unchanged. RitsuLib appends versioned, bounds-checked extension data to the original lobby messages, and every peer's capability is tracked even while the room has 1–4 players. Expansion is activated automatically when player 5 joins, but only if every existing player and the joining player support a compatible protocol. Otherwise the expansion attempt is rejected and both the host and a capable joining client receive a reason. Vanilla or old clients may join only while the current player count is below 4 and every existing player occupies a vanilla slot from 0 through 3. After a room contracts from an expanded state, clients that cannot restore the full roster are rejected while any high-slot survivor remains; admission is restored automatically after those slots are clear. Expansion remains unavailable while such a client stays.

The mod is always removed from vanilla multiplayer mod matching and uses its own capability handshake for expansion admission. The extra-player scaling multiplier follows the host during multiplayer.

Supports game 0.107.1, 0.111, and 0.113.

A persistent Multiplayer Limit Break indicator above the lobby player list reports Active or Disabled by incompatible clients together with the current player count and applicable limit. After five-player expansion activates, the text stays unchanged and the indicator changes from green to blue. Affected players receive a small amber warning triangle beside the nameplate without touching the vanilla ready or disconnected indicators.

Requires [STS2-RitsuLib](https://github.com/BAKAOLC/sts-2-ritsulib) 0.5.14 or later.

On Steam, room settings bring together invitations, visibility, and extra-player scaling. New rooms start friends-only; hosts can make a waiting room public, and scaling changes sync to guests. Share a Steam link or `MLB1` invitation code, then paste either into Enter invite on the join screen. Steam links also work without this mod; invite-code entry requires it. Room capacity and compatibility requirements still apply. Matching public rooms appear in the friend list and update automatically.

Up to 8 groups can be saved, with separate search and hosting-publication toggles. Group names are local labels; codes are case-sensitive and ignore surrounding whitespace. Save group edits to apply them. Matching codes help discover public rooms and do not act as passwords. Changing a match code does not remove players or revoke existing lobby links. Starting a run preserves the game's normal room-closing and rejoin behavior.

Invitation codes, match codes, and join input are hidden by default and concealed again when settings close or rooms change. These Steam features remain separate from the mod's multiplayer expansion support on other transports.

The basic idea for this project comes from [Rain156/sts2-RMP-Mods](https://github.com/Rain156/sts2-RMP-Mods).
