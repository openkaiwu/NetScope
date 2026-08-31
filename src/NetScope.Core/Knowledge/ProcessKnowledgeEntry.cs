namespace NetScope.Core.Knowledge;

/// <summary>
/// Windows 常见进程的内置知识条目：回答“这是什么、为什么在运行、高占用意味着什么、要不要动它”。
/// 仅描述身份与惯例行为，不推断当前机器上的因果关系。
/// </summary>
public sealed record ProcessKnowledgeEntry(
    string ExecutableName,
    string DisplayName,
    string Category,
    string Publisher,
    string Purpose,
    string HighUsageHint,
    string TerminationAdvice);
