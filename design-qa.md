# NetScope V0.1.3 诊断 / 测速分区 QA

## 视觉证据

- 独立网络诊断工作区：`design/qa/diagnostic-network-workspace-v013.png`
- 独立网速测试工作区：`design/qa/diagnostic-speed-workspace-v013.png`
- 920px 最小窗口对应的测速紧凑内容区：`design/qa/diagnostic-speed-compact-v013.png`
- 视觉基准：`design/reference/netscope-fluent-pro-option-3.png`

截图由 Release WPF 控件通过 `RenderTargetBitmap` 直接渲染，不是重新绘制的概念稿。视觉 QA 工具位于 `tests/NetScope.VisualQa`。

## 信息架构验收

- [x] “网络诊断”和“网速测试”成为两个同级二级工作区，可在诊断主入口内直接切换。
- [x] 网络诊断仅展示本机、网卡、IP/DHCP、网关、DNS、公网和目标的连通性证据，不出现吞吐测试卡片。
- [x] 网速测试拥有独立的开始、确认、取消、进度和结果区域，不再藏在诊断页底部。
- [x] 两个工作区各自只在对应任务运行时显示取消按钮，但底层仍通过同一取消命令安全终止当前任务。
- [x] 本地网卡协商速率明确标注为“链路速率 ≠ 公网实测”，防止把 100 Gbps 虚拟链路误读成公网速度。
- [x] 测速结果独立呈现下载、上传、空闲延迟、下载/上传负载延迟与 Bufferbloat。
- [x] 默认进入网络诊断；切换状态由 ViewModel 管理，不依赖页面代码隐藏业务状态。

## 数据语义验收

- [x] 网关趋势只包含 10 次网关 ICMP 成功样本，不混入 DNS、TCP、TLS 或阶段耗时。
- [x] DNS、TCP、TLS 与目标耗时仍保留在网络诊断工作区。
- [x] 测速结果仍会与 Wi-Fi、网关、DNS、VPN/代理证据关联，输出最可能瓶颈。
- [x] 快速诊断不下载或上传测速数据；真实测速仅在用户确认后运行。
- [x] 真实测速继续使用 HTTPS、约 62MB 流量上限、65 秒总时限和内存内结果。

## 响应式验收

- [x] 默认内容区 972×700 下两个工作区的标题、入口、主操作与结果均无重叠。
- [x] 紧凑内容区 712×560 下开始测速按钮和下载/上传结果保持可见，其余内容通过独立滚动查看。
- [x] 网络诊断在内容宽度低于 820px 时收起右侧证据栏，避免挤压主链路图。

## 自动化结果

- Release WPF 视觉 QA：0 错误、0 警告。
- 默认与紧凑内容区实际渲染成功。
- 单元与集成测试：33/33 通过。
- 在线测速协议烟雾测试：1/1 通过（约 246KB 下载/上传/延迟链路）。

## 已知边界

- 当前吞吐表示本机到 Cloudflare 边缘节点的当次表现，不等于运营商标称带宽。
- 网络诊断与测速共享网络快照和相关性证据，但运行状态、页面和主操作已分离。
- V0.1.3 尚不包含路由追踪、MTU、长期历史和逐进程带宽。

final result: passed

## V0.1.4 图标与默认首页增量验证

- [x] `NetScope.ico` 包含 16、20、24、32、40、48、64、128、256px 九种尺寸。
- [x] Release `NetScope.exe` 可通过 Windows Shell 成功提取 32px 应用图标：`design/qa/netscope-exe-icon-v014.png`。
- [x] 实际 WPF 主窗口标题栏使用新图标：`design/qa/netscope-default-port-v014.png`。
- [x] `MainViewModel` 默认页面为端口工作台，视觉 QA 会在导航不为“端口”时直接失败。
- [x] Inno Setup 安装程序和桌面/开始菜单快捷方式显式使用 `NetScope.exe` 的第 0 个图标资源。
- [x] 图标与安装使用说明：`docs/安装与使用说明.md`。

## V0.1.5 简约白底图标增量验证

- [x] 图标改为白色圆角方底、两枚蓝色连接块和青绿色状态点，无文字和快捷方式箭头。
- [x] 16、32、48px 实际 ICO 帧分别渲染检查通过：`design/qa/netscope-icon-v2-16.png`、`netscope-icon-v2-32.png`、`netscope-icon-v2-48.png`。
- [x] 旧版生成源保留为 `design/assets/netscope-icon-source-v1.png`，当前版本为 `netscope-icon-source-v2.png`。
- [x] 默认端口首页将使用新图标重新渲染为 `design/qa/netscope-default-port-v015.png`。
