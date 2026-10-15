using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using WinSshCopyId.UI;
using WinSshCopyId.Workspace;

namespace WinSshCopyId.Controls;

/// <summary>Optional inline guidance. Its synthetic draft never reads or invokes the originating control.</summary>
public sealed class GuidedControl : StackPanel
{
    private readonly Button entry;
    private readonly StackPanel panel=new(){Spacing=8,Visibility=Visibility.Collapsed};
    private readonly GuidanceDescriptor descriptor;
    private readonly Func<WorkspaceLanguage> language;
    public GuidedControl(UIElement control,GuidanceDescriptor descriptor,Func<WorkspaceLanguage> language)
    {
        this.descriptor=descriptor;this.language=language;Spacing=4;
        var ink=(Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["WorkspacePrimaryBrush"];
        var icon=new Grid{Width=18,Height=18};icon.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse{Stroke=ink,StrokeThickness=1.5});icon.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle{Width=2,Height=6,Fill=ink,Margin=new Thickness(0,5,0,0)});icon.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse{Width=2,Height=2,Fill=ink,Margin=new Thickness(0,0,0,8)});
        entry=new Button{Content=icon,Width=44,Height=44,Padding=new Thickness(0),HorizontalAlignment=HorizontalAlignment.Left,Style=(Style)Application.Current.Resources["MaterialButtonStyle"]};
        AutomationProperties.SetAutomationId(entry,descriptor.Id+".info");
        entry.Click+=(_,_)=>{if(panel.Visibility==Visibility.Visible)Close();else Open();};
        Children.Add(entry);Children.Add(panel);Children.Add(control);RefreshLanguage();
    }
    private string Local(string en,string yue)=>language() switch{WorkspaceLanguage.Cantonese=>yue,WorkspaceLanguage.Bilingual=>en+"\n"+yue,_=>en};
    public void RefreshLanguage()
    {
        var label=Local("Information about "+descriptor.Label,descriptor.CantoneseLabel+"的說明");AutomationProperties.SetName(entry,label);ToolTipService.SetToolTip(entry,label);var state=Local(panel.Visibility==Visibility.Visible?"Expanded":"Collapsed",panel.Visibility==Visibility.Visible?"已展開":"已收合");AutomationProperties.SetHelpText(entry,state);AutomationProperties.SetItemStatus(entry,state);
    }
    private void Open()
    {
        panel.Children.Clear();panel.Visibility=Visibility.Visible;
        panel.Children.Add(new TextBlock{Text=Local(descriptor.Explanation,descriptor.CantoneseExplanation),TextWrapping=TextWrapping.Wrap});
        panel.Children.Add(new TextBlock{Text=Local("Worked example: "+descriptor.Example+"\nExpected: "+descriptor.Expected,"操作範例："+descriptor.Example+"\n預期結果："+descriptor.Expected),TextWrapping=TextWrapping.Wrap});
        var draft=new TextBox{Header=Local("Synthetic playground input","虛構練習輸入"),Text=descriptor.Example,MaxLength=512};var result=new TextBlock{TextWrapping=TextWrapping.Wrap};
        var run=new Button{Content=Local("Try example","試用範例"),Style=(Style)Application.Current.Resources["MaterialButtonStyle"]};run.Click+=(_,_)=>result.Text=descriptor.Preview(draft.Text);
        var reset=new Button{Content=Local("Reset example","重設範例"),Style=(Style)Application.Current.Resources["MaterialButtonStyle"]};reset.Click+=(_,_)=>{draft.Text=descriptor.Example;result.Text="";};
        var close=new Button{Content=Local("Close guidance","關閉說明"),Style=(Style)Application.Current.Resources["MaterialButtonStyle"]};close.Click+=(_,_)=>Close();
        panel.Children.Add(draft);panel.Children.Add(run);panel.Children.Add(reset);panel.Children.Add(result);
        panel.Children.Add(new TextBlock{Text=Local("Practice only. No file, credential, connection, setting or history is changed. Complex multi-step workflow guidance remains incomplete.","只供練習，不會更改任何檔案、認證、連線、設定或歷史。複雜的多步驟操作指引仍未完成。"),TextWrapping=TextWrapping.Wrap});panel.Children.Add(close);RefreshLanguage();
        FrameworkElementAutomationPeer.FromElement(entry)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
    private void Close(){panel.Children.Clear();panel.Visibility=Visibility.Collapsed;RefreshLanguage();entry.Focus(FocusState.Programmatic);}
}
