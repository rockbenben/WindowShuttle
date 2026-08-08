using System.Windows.Markup;

namespace WindowShuttle.App.I18n;

// {loc:Loc Key_Name}。XAML 加载时取值 → 语言切换经重启生效。
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public string Key { get; set; } = "";
    public LocExtension() { }
    public LocExtension(string key) { Key = key; }
    public override object ProvideValue(IServiceProvider sp) => Strings.Get(Key);
}
