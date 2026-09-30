using UnityEngine;
using UnityEngine.UIElements;

// Recording mode, for videos: the app's controls leave the frame (the view bar, the tuck buttons,
// the song list, the transport, the tutorial's Back / Next / Skip and step count) and, in a
// vertical frame, the captions move up to about two thirds of the way down, clear of the space
// where Shorts and Reels put their titles and buttons. Other components write their own styles
// every frame, so the hiding is re-applied late in the frame while recording mode is on.
[DefaultExecutionOrder(3000)]
public sealed class RecordingMode : MonoBehaviour
{
    public static bool Active {get;private set;}
    // Where a vertical frame's captions start, as a fraction of the height.
    public const float CaptionTop=.62f;
    static readonly string[] Hidden={"controls","visualization-views","tuck-side-menu","tuck-song-library","song-library","rack-play-pause","load-progress","time-rack-seek","tutorial-back","tutorial-next","tutorial-skip","tutorial-progress","song-story-footer"};
    VisualElement root;readonly VisualElement[] found=new VisualElement[Hidden.Length];readonly bool[] hid=new bool[Hidden.Length];
    public void Bind(VisualElement ui){root=ui;}
    public static void Set(bool on){Active=on;}
    void OnDestroy(){Active=false;}
    void LateUpdate()
    {
        if(root==null)return;
        for(int i=0;i<Hidden.Length;i++)
        {
            found[i]??=root.Q(Hidden[i]);var e=found[i];if(e==null)continue;
            // Only what recording mode hid is shown again when it ends.
            if(Active){if(e.style.visibility!=Visibility.Hidden){e.style.visibility=Visibility.Hidden;hid[i]=true;}}
            else if(hid[i]){e.style.visibility=StyleKeyword.Null;hid[i]=false;}
        }
    }
}
