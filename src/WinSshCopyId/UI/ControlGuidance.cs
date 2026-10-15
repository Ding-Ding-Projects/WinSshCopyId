namespace WinSshCopyId.UI;

public sealed record GuidanceDescriptor(string Id,string Label,string CantoneseLabel,string Explanation,string CantoneseExplanation,string Example,string Expected,string Kind="preview")
{
    public string Preview(string input)=>Kind switch
    {
        "port"=>int.TryParse(input,out var port)&&port is >=1 and <=65535?$"Valid SSH port: {port}":"Enter a whole number from 1 to 65535.",
        "name"=>string.IsNullOrWhiteSpace(input)||input.Length>120?"Enter 1 to 120 characters.":"Display label: "+input.Trim(),
        "boolean"=>bool.TryParse(input,out var value)?"Preview state: "+(value?"On":"Off"):"Enter true or false.",
        "level"=>int.TryParse(input,out var level)&&level is >=1 and <=5?"Preview level: "+level:"Enter a whole number from 1 to 5.",
        _=>string.IsNullOrWhiteSpace(input)?"Enter the synthetic example value.":"Synthetic preview only: "+input.Trim()+". No real action was performed."
    };
}

public static class ControlGuidance
{
    public const string InventoryId="info-icon-guidance";
    public static readonly string[] RequiredIds=["connection.name","connection.hosts","connection.user","connection.port","connection.password","connection.remember","connection.install","connection.save","profile.import","profile.export","key.pick","key.generate","key.export","operation.cancel","result.retry","result.copy","result.export","settings.name","settings.english-humour","settings.cantonese-humour","settings.reduced-motion","settings.accessibility","settings.save"];
    public static IReadOnlyList<GuidanceDescriptor> All {get;} = [
        new("connection.name","Display name","顯示名稱","Name this saved connection. This changes its local label, not the SSH destination. Use 1 to 120 characters.","為已儲存連線命名。只會更改本機標籤，不會更改 SSH 目標。請輸入 1 至 120 個字元。","Build host","Display label: Build host","name"),
        new("connection.hosts","Hosts","主機","Enter one hostname or IP address per line, with an optional port. Review every target before installation. The example never connects.","每行輸入一個主機名稱或 IP 位址，可附加連接埠。安裝前請核對所有目標。範例不會連線。","example.test:22","Synthetic target preview only"),
        new("connection.user","Username","使用者名稱","Choose the account on the SSH destination. It must already exist; this tool does not create remote accounts.","輸入 SSH 目標上已存在的帳戶名稱。本工具不會建立遠端帳戶。","builder","Synthetic account preview only"),
        new("connection.port","Port","連接埠","Port 22 is the default. Enter a whole number from 1 to 65535; a per-host port overrides this default.","預設連接埠為 22。請輸入 1 至 65535 的整數；個別主機指定的連接埠會優先使用。","22","Valid SSH port: 22","port"),
        new("connection.password","Password","密碼","Enter the destination password only in the real password control. The example uses an account label, never a credential. Import clears the live password.","只可在真正的密碼欄輸入目標密碼。範例只使用帳戶標籤，絕不使用認證。匯入設定檔會清除現有密碼。","Example account","Synthetic account preview only"),
        new("connection.remember","Remember password","記住密碼","Opt in to Windows Credential Manager storage. Portable profiles never contain this credential. Turn this off and save to remove the remembered credential.","選擇後會使用 Windows 認證管理員儲存。可攜設定檔不包含認證。取消選擇並儲存即可移除已記住的認證。","false","Preview state: Off","boolean"),
        new("connection.install","Install public key","安裝公開金鑰","Choose a key, review all destinations, verify any new host fingerprint independently, then continue. Cancellation retains completed changes. This playground only previews a synthetic target.","先選擇金鑰並核對所有目標，再獨立核實新主機的指紋，然後繼續。取消不會復原已完成的變更。練習區只預覽虛構目標。","builder@example.test:22","Synthetic installation preview only"),
        new("connection.save","Save connection","儲存連線","Save the current label and connection metadata locally. A key reference remains untrusted until explicit selection. Password storage follows its separate opt-in.","將標籤及連線資料儲存在本機。金鑰路徑必須明確選擇後才可使用。密碼儲存由獨立選項控制。","Example connection","Synthetic save preview only"),
        new("profile.import","Import profile","匯入設定檔","Pick a profile JSON file. Import validates connection metadata, clears live credentials and selected keys, and never probes embedded key paths.","選擇 JSON 設定檔。匯入會驗證連線資料、清除現有認證及金鑰選擇，並且不會探查內附路徑。","example-profile.json","Synthetic import preview only"),
        new("profile.export","Export profile","匯出設定檔","Export connection metadata to JSON. Credentials are omitted. Replacing a destination requires the separate destructive confirmation.","將連線資料匯出為 JSON，不包含認證。取代現有檔案需要另行確認。","example-profile.json","Synthetic export preview only"),
        new("key.pick","Choose public key","選擇公開金鑰","Use the native picker to deliberately select a public key. Its bytes and algorithm are validated. Never choose or paste a private key here.","使用原生檔案選擇器明確選擇公開金鑰，內容及演算法會經過驗證。請勿在此選擇或貼上私人金鑰。","example.pub","Synthetic key selection preview only"),
        new("key.generate","Generate key","產生金鑰","Choose an available algorithm and a new absolute output basename. Existing keys are never overwritten. The actual service verifies the pair and private permissions.","選擇可用演算法及新的完整輸出路徑。現有金鑰不會被覆寫。實際服務會驗證金鑰組及私人檔案權限。","example-key","Synthetic generation preview only"),
        new("key.export","Export public key","匯出公開金鑰","Export only the selected public-key line. Private-key contents never enter this export. Review an existing destination before replacement.","只匯出已選取的公開金鑰內容，不包含私人金鑰。取代現有檔案前請先核對目標。","example.pub","Synthetic public export preview only"),
        new("operation.cancel","Cancel operation","取消操作","Request cancellation and wait for terminal settlement. Completed remote changes remain installed; cancellation is not rollback.","要求取消後，請等待操作結束。已完成的遠端變更會保留；取消並不代表復原。","Active example operation","Synthetic cancellation preview only"),
        new("result.retry","Retry failed targets","重試失敗目標","Only eligible failed targets are retried. Verification-only retry does not install again. Changed identities and uncertain writes require investigation.","只會重試合資格的失敗目標。只重試驗證時不會再次安裝。身分變更及結果不明的寫入必須先調查。","Example failed target","Synthetic retry preview only"),
        new("result.copy","Copy command","複製指令","Copy commands for selected or filtered results with known local private-key paths. Commands use PowerShell-safe quoting and contain no password.","為已選取或已篩選且有本機私人金鑰路徑的結果複製指令。指令使用適用於 PowerShell 的安全引號，不包含密碼。","Example selected result","Synthetic command preview only"),
        new("result.export","Export results","匯出結果","Export selected results, or the filtered view when no rows are selected. Installation and verification outcomes remain separate; credentials are omitted.","匯出已選取的結果；若未選取任何列，則匯出已篩選內容。安裝及驗證結果分開記錄，不包含認證。","Example filtered results","Synthetic result export preview only"),
        new("settings.name","Application display name","程式顯示名稱","Change the visible name only. Package identity, update feed and application-data directory stay unchanged.","只更改可見名稱，不會更改套件身分、更新來源或資料目錄。","SSH workspace","Display label: SSH workspace","name"),
        new("settings.english-humour","English funny level","英文趣味程度","Levels 1 to 5 alter English message tone, never operation facts. Default is 5. Complete message coverage remains under implementation.","1 至 5 級只更改英文訊息語氣，不會改變操作事實。預設為 5。完整訊息覆蓋仍在實作。","5","Preview level: 5","level"),
        new("settings.cantonese-humour","Cantonese funny level","廣東話趣味程度","Levels 1 to 5 alter Cantonese message tone independently of English. Default is 5. Facts and safety consequences stay exact.","1 至 5 級獨立控制廣東話訊息語氣，預設為 5。事實及安全後果保持準確。","5","Preview level: 5","level"),
        new("settings.reduced-motion","Reduced motion","減少動態效果","Request reduced motion while retaining authored preferences. Native operating-system accessibility preferences remain authoritative.","要求減少動態效果，同時保留原有設定。作業系統的無障礙偏好仍然優先。","true","Preview state: On","boolean"),
        new("settings.accessibility","Show accessibility control","顯示無障礙控制項","Show or hide the title-bar accessibility entry. This setting remains reachable to restore it later.","顯示或隱藏標題列的無障礙入口。日後仍可在此重新顯示。","true","Preview state: On","boolean"),
        new("settings.save","Save settings","儲存設定","Validate and persist preferences, then update the existing controls in place. A save does not recreate the connection form or erase its draft.","驗證並儲存偏好後，直接更新現有控制項。儲存不會重新建立連線表單或刪除草稿。","Example preferences","Synthetic settings preview only")
    ];
    public static GuidanceDescriptor Get(string id)=>All.Single(x=>x.Id==id);
    public static void Validate(IEnumerable<GuidanceDescriptor> inventory)
    {
        var rows=inventory.ToArray();
        if(rows.Select(x=>x.Id).Distinct().Count()!=rows.Length||RequiredIds.Any(id=>!rows.Any(x=>x.Id==id))||rows.Any(x=>string.IsNullOrWhiteSpace(x.Label)||string.IsNullOrWhiteSpace(x.CantoneseLabel)||string.IsNullOrWhiteSpace(x.Explanation)||string.IsNullOrWhiteSpace(x.CantoneseExplanation)||string.IsNullOrWhiteSpace(x.Example)||string.IsNullOrWhiteSpace(x.Expected)))throw new InvalidDataException("The info-icon-guidance inventory is incomplete.");
    }
    public static void ValidateAttachments(IEnumerable<string> attached)
    {var ids=attached.ToHashSet(StringComparer.Ordinal);if(RequiredIds.Any(id=>!ids.Contains(id)))throw new InvalidDataException("A required info-icon guidance entry is not attached to its control.");}
}
