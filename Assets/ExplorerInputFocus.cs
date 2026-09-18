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
        (document?.rootVisualElement?.focusController?.focusedElement as VisualElement)?.Blur();
        ViewportOwnsKeyboard=true;
    }
    public void Bind(UIDocument ui)
    {
        document=ui; ViewportOwnsKeyboard=true;
        var root=ui.rootVisualElement;
        root.RegisterCallback<FocusInEvent>(_=>ClaimUI());
        root.RegisterCallback<PointerDownEvent>(_=>ClaimUI(),TrickleDown.TrickleDown);
        root.RegisterCallback<NavigationMoveEvent>(e=>{if(ViewportOwnsKeyboard)e.StopImmediatePropagation();},TrickleDown.TrickleDown);
        root.RegisterCallback<NavigationSubmitEvent>(e=>{if(ViewportOwnsKeyboard)e.StopImmediatePropagation();},TrickleDown.TrickleDown);
        root.RegisterCallback<NavigationCancelEvent>(e=>{if(ViewportOwnsKeyboard)e.StopImmediatePropagation();},TrickleDown.TrickleDown);
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
    }
    void OnApplicationFocus(bool focus){if(!focus)ClaimUI();}
    void OnDestroy(){document=null;ViewportOwnsKeyboard=true;}
}
