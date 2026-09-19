using System.Collections.Generic;
using UnityEngine.UIElements;

public sealed class SideMenuTabs : TabView
{
    readonly Dictionary<string,Tab> tabs=new();
    readonly Dictionary<string,ScrollView> pages=new();
    readonly VisualElement transport;
    public string Selected {get;private set;}
    public SideMenuTabs(VisualElement sharedTransport)
    {
        transport=sharedTransport;name="side-menu-tabs";AddToClassList("side-tabs");reorderable=false;
        activeTabChanged+=(_,tab)=>Activate(tab);
    }
    public ScrollView AddPage(string id,string title)
    {
        var tab=new Tab(title){name="tab-"+id,tooltip=title};
        var page=new ScrollView{name="page-"+id};page.AddToClassList("side-tab-page");
        tabs.Add(id,tab);pages.Add(id,page);tab.Add(page);Add(tab);
        if(tabs.Count==1)Select(id);else Activate(activeTab);
        return page;
    }
    public void Select(string id){if(!tabs.TryGetValue(id,out var tab))return;activeTab=tab;Activate(tab);}
    void Activate(Tab tab)
    {
        if(tab==null)return;
        tab.Insert(0,transport);
        foreach(var item in tabs){bool active=item.Value==tab;if(active)Selected=item.Key;pages[item.Key].style.display=active?DisplayStyle.Flex:DisplayStyle.None;}
    }
}
