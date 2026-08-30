namespace Mailcoded.Bench.Corpus;

/// <summary>Fixed word lists. Every entry is load-bearing for the corpus digest: never reorder.</summary>
public static class CorpusVocabulary
{
    /// <summary>The term <c>FtsSearch_CommonTerm</c> measures; it lands in roughly one body in six.</summary>
    public const string CommonTerm = "invoice";

    /// <summary>The phrase <c>FtsSearch_Phrase</c> measures, emitted verbatim and contiguously.</summary>
    public const string Phrase = "quarterly revenue forecast";

    public static readonly string[] SubjectWords =
    [
        "invoice", "renewal", "release", "roadmap", "incident", "postmortem", "onboarding", "budget",
        "forecast", "migration", "outage", "retrospective", "contract", "shipment", "quarterly",
        "revenue", "staffing", "compliance", "escalation", "handover", "backlog", "deployment",
        "credentials", "throughput", "latency", "storage", "billing", "renewal", "downtime", "audit",
    ];

    public static readonly string[] BodyWords =
    [
        "the", "team", "reviewed", "attached", "figures", "before", "the", "close", "of", "business",
        "please", "confirm", "receipt", "and", "flag", "anything", "that", "looks", "wrong", "in",
        "the", "summary", "we", "expect", "the", "next", "revision", "to", "land", "on", "friday",
        "invoice", "totals", "were", "recalculated", "after", "the", "credit", "note", "was", "applied",
        "storage", "growth", "remains", "within", "the", "projected", "envelope", "for", "this", "quarter",
        "latency", "regressed", "slightly", "on", "the", "european", "region", "during", "the", "rollout",
        "quarterly", "revenue", "forecast", "will", "be", "circulated", "once", "finance", "signs", "off",
        "naïve", "café", "résumé", "Zürich", "São", "Paulo",
    ];

    public static readonly string[] CjkSubjectWords =
    [
        "請求書", "見積", "納品", "契約", "会議", "報告書", "四半期", "予算", "障害", "対応",
        "发票", "报价", "合同", "会议", "季度", "预算", "故障", "处理", "发货", "报告",
        "청구서", "견적", "계약", "회의", "분기", "예산", "장애", "대응",
    ];

    public static readonly string[] CjkBodyWords =
    [
        "本日の会議で決まった内容を共有します", "添付の請求書をご確認ください", "四半期の予算を見直しました",
        "障害の原因は設定の誤りでした", "納品予定日は来週の金曜日です",
        "请查收附件中的发票与报价单", "本季度预算已经完成复核", "故障原因是配置错误导致的",
        "발주서를 첨부하오니 확인 부탁드립니다", "이번 분기 예산안을 검토했습니다",
    ];

    public static readonly string[] Domains =
    [
        "example.test", "contoso.test", "northwind.test", "fabrikam.test", "adventure.test",
        "kestrel.test", "lumen.test", "orchard.test",
    ];

    public static readonly string[] People =
    [
        "avery", "blake", "casey", "devon", "ellis", "frankie", "gray", "harper", "indigo", "jules",
        "kai", "logan", "morgan", "noor", "quinn", "reese", "sasha", "toma", "vega", "wren",
    ];

    /// <summary>Folder layout and the share of the corpus each folder holds; the shares sum to 1.</summary>
    public static readonly (string Path, double Share)[] Folders =
    [
        ("INBOX", 0.55),
        ("Archive", 0.20),
        ("Sent", 0.10),
        ("Lists/engineering", 0.06),
        ("Lists/announcements", 0.04),
        ("Projects/atlas", 0.03),
        ("Junk", 0.015),
        ("Drafts", 0.005),
    ];
}
