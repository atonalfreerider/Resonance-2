using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

// One owner for shortcuts. UI ownership persists until the viewport is clicked.
// Poll before gameplay Update, since UI Toolkit dispatches events later in the frame.
[DefaultExecutionOrder(-1000)]
public class ExplorerInputFocus : MonoBehaviour
{
    public static bool ViewportOwnsKeyboard { get; private set; } = true;
    static UIDocument document;
    public static bool PointerInViewport => Camera.main!=null && Mouse.current!=null && Camera.main.pixelRect.Contains(Mouse.current.position.ReadValue());
    public static void ClaimUI()
    {
        ViewportOwnsKeyboard=false;
        if(document!=null) document.GetComponent<InputHandler>()?.ReleaseKeyboardNotes();
    }
    public static void ClaimViewport()
    {
        ViewportOwnsKeyboard=true;
        document?.rootVisualElement?.Focus();
    }
    public void Bind(UIDocument ui)
    {
        document=ui; ViewportOwnsKeyboard=true;
        var root=ui.rootVisualElement;
        root.focusable=true;root.tabIndex=-1;
        // Navigation can assign focus before delivering its event. Focus alone must
        // never transfer ownership: only an actual panel click or Tab does that.
        root.RegisterCallback<FocusInEvent>(e=>{if(ViewportOwnsKeyboard&&e.target!=root)root.schedule.Execute(()=>{if(ViewportOwnsKeyboard)root.Focus();});});
        root.RegisterCallback<PointerDownEvent>(_=>ClaimUI(),TrickleDown.TrickleDown);
        root.RegisterCallback<NavigationMoveEvent>(e=>{if(ViewportOwnsKeyboard){root.focusController.IgnoreEvent(e);e.StopImmediatePropagation();}},TrickleDown.TrickleDown);
        root.RegisterCallback<NavigationSubmitEvent>(e=>{if(ViewportOwnsKeyboard){root.focusController.IgnoreEvent(e);e.StopImmediatePropagation();}},TrickleDown.TrickleDown);
        root.RegisterCallback<NavigationCancelEvent>(e=>{if(ViewportOwnsKeyboard){root.focusController.IgnoreEvent(e);e.StopImmediatePropagation();}},TrickleDown.TrickleDown);
        root.RegisterCallback<KeyDownEvent>(e=>{if(ViewportOwnsKeyboard&&e.keyCode!=KeyCode.Tab){root.focusController.IgnoreEvent(e);e.StopImmediatePropagation();}},TrickleDown.TrickleDown);
    }
    void Update()
    {
        var mouse=Mouse.current;
        if(mouse!=null && (mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame))
        {
            if(PointerInViewport)ClaimViewport();else ClaimUI();
        }
        // Tab always enters UI navigation before any simultaneous musical shortcut.
        if(Keyboard.current!=null && Keyboard.current.tabKey.wasPressedThisFrame)ClaimUI();
        if(ViewportOwnsKeyboard&&document!=null&&document.rootVisualElement.focusController.focusedElement!=document.rootVisualElement)document.rootVisualElement.Focus();
    }
    void OnApplicationFocus(bool focus){if(!focus)ClaimUI();}
    void OnDestroy(){document=null;ViewportOwnsKeyboard=true;}
}
