using WinSshCopyId.Workspace;
namespace WinSshCopyId.Localization;

public static class WorkspaceText
{
    private static readonly Dictionary<string,string> Cantonese = new()
    {
        ["Settings saved."]="設定已儲存。",
        ["The profile key reference was not opened. Choose a public key explicitly."]="未有開啟設定檔內的金鑰路徑。請自行選擇公開金鑰。",
        ["Profile imported. Its key path was not opened. Choose a public key explicitly and enter the password before deployment."]="設定檔已匯入，但未有開啟內附的金鑰路徑。部署前請自行選擇公開金鑰並輸入密碼。",
        ["No saved connections. Add a connection or import a profile."]="未有已儲存連線。請新增連線或匯入設定檔。",
        ["No public keys. Import a public key or generate a new pair."]="未有公開金鑰。請匯入公開金鑰或產生新金鑰組。",
        ["No results yet. Start an installation from Connections."]="未有結果。請先在連線頁面開始安裝。",
        ["Connections"]="連線",["Keys"]="金鑰",["Results"]="結果",["Tools"]="工具",["History"]="歷史",["Settings"]="設定",["Help"]="說明",
        ["Commands"]="指令",["Accessibility"]="無障礙",["Workspace tabs"]="工作區分頁",["Search connections"]="搜尋連線",["Search options"]="搜尋選項",["Search keys"]="搜尋金鑰",["Search results"]="搜尋結果",
        ["New connection"]="新增連線",["Save connection"]="儲存連線",["Import profile"]="匯入設定檔",["Export profile"]="匯出設定檔",["Display name"]="顯示名稱",["Hosts"]="主機",["Username"]="使用者名稱",["Port"]="連接埠",["Public key"]="公開金鑰",["Choose public key"]="選擇公開金鑰",
        ["Password"]="密碼",["Remember password in Credential Manager"]="在認證管理員記住密碼",["Install public key"]="安裝公開金鑰",["Cancel"]="取消",["Continue"]="繼續",["Close"]="關閉",["Approve identity"]="批准身分",["Host identity approval"]="核對主機身分",["Review installation"]="檢查安裝資料",
        ["Generate key"]="產生金鑰",["Import public key"]="匯入公開金鑰",["Export public key"]="匯出公開金鑰",["Browse"]="瀏覽",["Comment"]="備註",["Output path"]="輸出路徑",["Algorithm"]="演算法",["Refresh"]="重新整理",["Copy command"]="複製指令",["Retry failed targets"]="重試失敗目標",["Export results"]="匯出結果",
        ["No saved connections"]="未有已儲存連線",["No public keys"]="未有公開金鑰",["No results yet"]="未有結果",["No matching records"]="沒有符合的記錄",["Choose a record to inspect it."]="選擇記錄以查看詳情。",["Theme"]="主題",["Language"]="語言",["Density"]="密度",["Light"]="淺色",["Dark"]="深色",["System"]="跟隨系統",["English"]="英文",["Cantonese"]="廣東話",["Bilingual"]="雙語",["Comfortable"]="舒適",["Compact"]="緊密",["Spacious"]="寬鬆",["English funny level"]="英文趣味程度",["Cantonese funny level"]="廣東話趣味程度",["Reduced motion"]="減少動態效果",["Save settings"]="儲存設定",["Show accessibility control"]="顯示無障礙控制項",["Local workspace"]="本機工作區",["Evidence profile: SSH and credential storage disabled"]="驗證設定檔：SSH 及認證儲存已停用",["No credential is included in a portable profile."]="可攜設定檔不包含任何認證。"
    };
    public static string Get(string text, WorkspaceLanguage language)
    {
        if (!Cantonese.TryGetValue(text,out var translated)) return text;
        return language switch {WorkspaceLanguage.Cantonese=>translated,WorkspaceLanguage.Bilingual=>text+"\n"+translated,_=>text};
    }
    public static string Ready(WorkspaceSettings settings)
    {
        string[] en=["Ready.","Ready when you are.","Ready. Choose a destination.","Ready. The key has somewhere to go.","Ready. The key is packed; choose its destination."];
        string[] yue=["準備就緒。","準備好，可以開始。","準備好，請選擇目標。","準備好，金鑰等緊目的地。","準備好，金鑰執好行李，請揀目的地。"];
        return settings.Language switch {WorkspaceLanguage.Cantonese=>yue[settings.CantoneseFunnyLevel-1],WorkspaceLanguage.Bilingual=>en[settings.EnglishFunnyLevel-1]+" · "+yue[settings.CantoneseFunnyLevel-1],_=>en[settings.EnglishFunnyLevel-1]};
    }
}
