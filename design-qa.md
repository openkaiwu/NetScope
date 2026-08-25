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

## V0.2 性能事件记录与归因 QA

### 视觉证据

- 性能工作区三个子页（由 Release WPF 控件直接渲染）：总览 `design/qa/netscope-performance-v020.png`、事件时间线 `design/qa/netscope-performance-events-v020.png`、进程中心 `design/qa/netscope-performance-processes-v020.png`

### 信息架构验收

- [x] 新增“性能”入口，位于“端口”与“诊断”之间；默认首页仍是端口工作台。
- [x] 性能工作区含总览、事件、进程三个二级页面。
- [x] 总览展示 CPU/内存/网络曲线、Top 进程、“刚才卡了”按钮和最近事件。
- [x] 事件详情展示事件前 30 秒 → 发生中 → 后 30 秒系统上下文、证据、最可能原因、可信度、建议与嫌疑进程。
- [x] 进程中心支持搜索，展示时间曲线、相关事件与端口占用。
- [x] “刚才卡了”点击后立即向事件流追加用户标记事件，事件页即时可见。
- [x] 全部结论使用“可能/疑似”措辞并附可信度；无结束进程等破坏性操作。

### 数据语义验收

- [x] 事件规则 4 条（CPU 争用、内存压力、IO 压力、网络劣化）+ 用户标记，状态机 正常→疑似→捕捉中→冷却。
- [x] 影响度评分排序嫌疑进程（CPU+内存+IO+前台+用户标记邻近度），只排序证据、不下因果结论。
- [x] 历史存储使用进程身份 = PID + 启动时间（同一 PID 不同实例分别记录）。
- [x] SQLite/WAL、批量写入、30 秒降采样、保留天数 1/7/14/30、损坏恢复（保留损坏副本并重建）。
- [x] 设置项（历史开关/保留天数/后台采样）约 15 秒内热加载。

### 冒烟测试发现并修复的生产级缺陷

- 进程名乱码（`潔敄歳攮數`）：`Process32First` 未指定 `CharSet.Unicode` 绑定到 ANSI 导出，ASCII 字节按 UTF-16 解码；改用 `Process32FirstW/Process32NextW` + `ExactSpelling` 修复。
- 可用内存恒为 0 且误报内存压力：`GlobalMemoryStatusEx` 以 `[In, Out]` 结构体传值返回 ok 但不回写；改用 `ref` 修复。
- “刚才卡了”被拒但事件仍创建：服务端把 `MarkLagDto` 错序列化为 `PerformanceEventDto`，客户端无法解析；修复服务端响应类型。
- 网卡名称为空：`SystemSampleDto` 丢失 `NetworkLinkUp/NetworkAdapterName`，`ProcessSampleDto` 丢失 `IsForeground`；补回 DTO 与映射。
- 发布包缺 `NetScope.Collector.exe`：App 的 `StageCollector` 目标在发布时跳过；`scripts/package.ps1` 增加 Collector 独立发布到同一目录，并校验 exe 存在。

### 记录到的设计偏差

- 历史持久化仅保留按影响度排序的 Top 25 进程（5 秒粒度），不记录全部进程。
- 突发采样是全局模式（触发期间所有进程 500ms），不是按进程独立触发。
- 网络劣化规则只使用被动网卡状态（链路通断/适配器名），不做主动探测，因此通常只产生被动状态证据。
- 历史记录默认开启（计划文档未指定默认值），UI 顶部展示保留天数提示与关闭入口。

### 自动化结果

- 单元与集成测试：90/90 通过（新增性能事件引擎 11、SQLite 历史存储 9、影响度评分 13 共 33 个 V0.2 测试）。
- Release 全量发布成功；已发布 Collector 的 IPC 冒烟（系统/进程/端口采样、markLag、事件、历史查询、DB 落盘）通过。
- 便携 ZIP 与 Inno Setup 安装包均包含 `NetScope.Collector.exe`。
