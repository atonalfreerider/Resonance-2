using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

// History pictures for a prepared story: a cue's image (a freely licensed JPEG/PNG in the
// bundle) pops up as a captioned card with its licence credit while the cue plays. The files
// are read off the main thread when the story loads and decoded one per frame; during playback
// only the card's opacity and scale change, and only while it fades.
[DefaultExecutionOrder(1110)]
public sealed class StoryPictures : MonoBehaviour
{
    SongDirector director;VisualizationViews views;SongLibraryPanel library;
    VisualElement root,controls,card,picture;Label caption,credit;
    readonly Dictionary<string,Texture2D> textures=new(StringComparer.OrdinalIgnoreCase);
    readonly Queue<KeyValuePair<string,byte[]>> decode=new();
    Task<List<KeyValuePair<string,byte[]>>> pending;int revision=-1;
    string shown,wanted,loadedFolder;VisualizationViews.View lastView;int suppressedCue=-1,lastCue=-1;TutorialDirector tutorial;MidiPlayer clock;float alpha,placedAlpha=-1,placedWidth,placedHeight,placedLeft=-1,placedDock=-1;string placedPlacement;
    public string Shown=>alpha>.5f?shown:null;
    public int Loaded=>textures.Count;
    const float FadeSeconds=.45f;

    // A bundle-relative picture path, or null when it would leave the bundle or is not a picture.
    public static string Resolve(string folder,string image)
    {
        if(string.IsNullOrWhiteSpace(folder)||string.IsNullOrWhiteSpace(image)||Path.IsPathRooted(image))return null;
        string ext=Path.GetExtension(image).ToLowerInvariant();if(ext!=".jpg"&&ext!=".jpeg"&&ext!=".png")return null;
        string root=Path.GetFullPath(folder),full=Path.GetFullPath(Path.Combine(root,image));
        return full.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)&&File.Exists(full)?full:null;
    }
    public void Bind(VisualElement ui,VisualElement panel)
    {
        controls=panel;director=GetComponent<SongDirector>();views=GetComponent<VisualizationViews>();library=GetComponent<SongLibraryPanel>();root=ui;
        card=new VisualElement{name="story-picture",pickingMode=PickingMode.Ignore};
        card.style.position=Position.Absolute;card.style.paddingLeft=card.style.paddingRight=card.style.paddingTop=8;card.style.paddingBottom=9;
        card.style.backgroundColor=new Color(.035f,.07f,.115f,.92f);
        card.style.borderTopLeftRadius=card.style.borderTopRightRadius=card.style.borderBottomLeftRadius=card.style.borderBottomRightRadius=12;
        card.style.borderLeftWidth=card.style.borderRightWidth=card.style.borderTopWidth=card.style.borderBottomWidth=1;
        card.style.borderLeftColor=card.style.borderRightColor=card.style.borderBottomColor=new Color(.5f,.72f,.92f,.22f);card.style.borderTopColor=new Color(.55f,.78f,.96f,.45f);
        picture=new VisualElement{pickingMode=PickingMode.Ignore};
        picture.style.borderTopLeftRadius=picture.style.borderTopRightRadius=picture.style.borderBottomLeftRadius=picture.style.borderBottomRightRadius=7;
        picture.style.backgroundColor=new Color(.08f,.1f,.14f);picture.style.alignSelf=Align.Center;
#if UNITY_2022_2_OR_NEWER
        picture.style.backgroundSize=new BackgroundSize(BackgroundSizeType.Cover);
#endif
        card.Add(picture);
        caption=new Label{pickingMode=PickingMode.Ignore,enableRichText=false};caption.style.whiteSpace=WhiteSpace.Normal;caption.style.fontSize=14;
        caption.style.color=new Color(.93f,.96f,1);caption.style.marginTop=7;caption.style.marginLeft=caption.style.marginRight=2;caption.style.unityFontStyleAndWeight=FontStyle.Bold;card.Add(caption);
        credit=new Label{pickingMode=PickingMode.Ignore,enableRichText=false};credit.style.whiteSpace=WhiteSpace.Normal;credit.style.fontSize=10;
        credit.style.color=new Color(.62f,.72f,.82f);credit.style.marginTop=2;credit.style.marginLeft=credit.style.marginRight=2;card.Add(credit);
        card.style.opacity=0;card.style.display=DisplayStyle.None;root.Add(card);
    }
    void Load()
    {
        revision=director.Revision;Clear();
        var story=director.Current;string folder=director.Folder;loadedFolder=story!=null?folder:null;
        if(story?.cues==null||string.IsNullOrEmpty(folder))return;
        var images=story.cues.Where(c=>!string.IsNullOrEmpty(c.image)).Select(c=>c.image).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if(images.Count==0)return;
        pending=Task.Run(()=>{var files=new List<KeyValuePair<string,byte[]>>();foreach(var image in images){string full=Resolve(folder,image);if(full!=null)files.Add(new(image,File.ReadAllBytes(full)));}return files;});
    }
    // A new song or story drops the card at once: no fade, nothing carried into the new view.
    void Clear()
    {
        pending=null;decode.Clear();shown=wanted=null;alpha=0;placedAlpha=0;placedWidth=-1;loadedFolder=null;suppressedCue=-1;
        foreach(var t in textures.Values)if(t!=null)Destroy(t);textures.Clear();
        if(card!=null){picture.style.backgroundImage=StyleKeyword.None;card.style.opacity=0;card.style.display=DisplayStyle.None;}
    }
    void Update()
    {
        if(director==null)return;
        if(revision!=director.Revision)Load();
        if(pending!=null&&pending.IsCompleted){
            if(pending.IsFaulted)Debug.LogWarning("Story pictures unavailable: "+pending.Exception?.GetBaseException().Message);
            else foreach(var file in pending.Result)decode.Enqueue(file);
            pending=null;
        }
        // One decode a frame keeps a story's pictures from stalling a frame together.
        if(decode.Count>0){var file=decode.Dequeue();var texture=new Texture2D(2,2,TextureFormat.RGBA32,false){name="Story picture "+file.Key,wrapMode=TextureWrapMode.Clamp};
            if(texture.LoadImage(file.Value,true))textures[file.Key]=texture;else Destroy(texture);}
    }
    void LateUpdate()
    {
        if(card==null||root.panel==null)return;
        var cue=director.CurrentCue;
        // A change of view clears the picture at once. Within a cue (the viewer changed it), that
        // cue's picture stays away; at a cue's start (the story changed it), the new cue's picture
        // fades in fresh rather than the old one carrying into the new view.
        bool cueChanged=director.CueIndex!=lastCue;
        if(views.Current!=lastView)
        {
            lastView=views.Current;
            if(shown!=null||alpha>0){if(!cueChanged)suppressedCue=director.CueIndex;shown=null;alpha=0;placedAlpha=0;card.style.display=DisplayStyle.None;card.style.opacity=0;}
        }
        if(cueChanged&&director.CueIndex!=suppressedCue)suppressedCue=-1;
        lastCue=director.CueIndex;
        tutorial??=GetComponent<TutorialDirector>();
        // Only this song's pictures, only while its story directs and no tutorial runs: anything else hides immediately.
        if(cue==null||loadedFolder==null||!string.Equals(loadedFolder,director.Folder,StringComparison.OrdinalIgnoreCase)||director.CueIndex==suppressedCue||(tutorial!=null&&tutorial.Playing)){
            if(shown!=null||alpha>0||placedAlpha!=0){shown=null;alpha=0;placedAlpha=0;card.style.display=DisplayStyle.None;card.style.opacity=0;}
            return;
        }
        // A picture stays about a second past the narration it illustrates (eight seconds at most
        // without one), not for the whole cue.
        clock??=GetComponent<MidiPlayer>();
        double until=Math.Min(cue.end,cue.narrationEnd>0?Math.Min(cue.narrationEnd+1.2,cue.start+14):cue.start+8);
        bool timely=clock==null||clock.Position<until;
        wanted=timely&&!string.IsNullOrEmpty(cue.image)&&textures.ContainsKey(cue.image)?cue.image:null;
        float step=Main.ReducedMotion?1:Time.unscaledDeltaTime/FadeSeconds;
        // Fade out whatever is up before the next picture takes the card.
        if(shown!=wanted){alpha=Mathf.MoveTowards(alpha,0,step);if(alpha<=0){shown=wanted;if(shown!=null)Fill(cue);}}
        else if(shown!=null)alpha=Mathf.MoveTowards(alpha,1,step);
        if(shown==null&&alpha<=0){if(placedAlpha!=0){card.style.display=DisplayStyle.None;card.style.opacity=0;placedAlpha=0;}return;}
        Place(cue);
        if(alpha!=placedAlpha){
            placedAlpha=alpha;float eased=alpha*alpha*(3-2*alpha);
            card.style.display=DisplayStyle.Flex;card.style.opacity=eased;
            card.style.scale=new Scale(Vector3.one*(Main.ReducedMotion?1:.94f+.06f*eased));
            card.style.translate=new Translate(0,Main.ReducedMotion?0:(1-eased)*10);
        }
    }
    string placement="";
    VisualizationViews.View placedView;Rect placedStrip;bool placedVertical,placedInstrumental;
    void Fill(SongDirector.Cue cue)
    {
        var texture=textures[shown];picture.style.backgroundImage=new StyleBackground(texture);
        caption.text=cue.imageCaption??"";caption.style.display=string.IsNullOrEmpty(caption.text)?DisplayStyle.None:DisplayStyle.Flex;
        credit.text=cue.imageCredit??"";placement=cue.imagePlacement??"";placedWidth=-1;
    }
    // Placed where the featured view leaves room, below the view bar and above the caption:
    // in the overview, the lyric column above the strip (between the wheels and the torus); in
    // lyric mode, the top right, down to the strip's midline; otherwise the top right, beside the
    // drum wheel or the pattern wheels. In a stacked torus view, a compact card uses the upper
    // corner instead of covering either the coiled torus or the uncoiled circle of fifths.
    // A cue can ask for right, left or centre instead.
    // Styles are written only when the viewport, the view, the side panels or the picture change.
    const float Top=64,Chrome=74,MinCardWidth=250;
    void Place(SongDirector.Cue cue)
    {
        float width=root.resolvedStyle.width,height=root.resolvedStyle.height;
        if(!float.IsFinite(width)||width<1||height<1)return;
        float left=views.PanelHidden?20:controls.resolvedStyle.width+20,dock=library!=null?library.DockedWidth:0;
        var view=views.Current;var strip=views.LyricStrip;
        bool instrumental=(clock?.HarmonicPrepared?.Lyrics?.Lines?.Length??0)==0;
        if(width==placedWidth&&height==placedHeight&&left==placedLeft&&dock==placedDock&&placement==placedPlacement&&view==placedView&&strip==placedStrip&&views.Vertical==placedVertical&&instrumental==placedInstrumental)return;
        placedInstrumental=instrumental;
        placedVertical=views.Vertical;
        placedWidth=width;placedHeight=height;placedLeft=left;placedDock=dock;placedPlacement=placement;placedView=view;placedStrip=strip;
        var texture=shown!=null&&textures.TryGetValue(shown,out var t)?t:null;
        float aspect=texture!=null?Mathf.Clamp(texture.width/(float)Mathf.Max(1,texture.height),.55f,2.2f):1.4f;
        bool stacked=views.Vertical||height>width*1.05f;
        // Without a lyric column, centre the portrait in the usable viewport in either
        // orientation. Leave the view selector above and the narration caption below.
        if(instrumental)
        {
            float availableWidth=Mathf.Max(1,width-dock-left-20),availableHeight=Mathf.Max(1,height-Top-110);
            float iw=Mathf.Min(availableWidth*.65f,stacked?320:400),ih=iw/aspect;
            float limit=Mathf.Min(availableHeight-Chrome,height*(stacked?.28f:.4f));
            if(ih>Mathf.Max(1,limit)){ih=Mathf.Max(1,limit);iw=ih*aspect;}
            float cw=Mathf.Min(availableWidth,Mathf.Max(iw+16,MinCardWidth));
            picture.style.width=iw;picture.style.height=ih;card.style.width=cw;
            card.style.left=left+(availableWidth-cw)*.5f;
            card.style.top=Top+Mathf.Max(0,(availableHeight-ih-Chrome)*.5f);
            return;
        }
        // A portrait torus is centred well below the top chrome. Keep history pictures compact
        // in that unused upper corner so neither the torus nor its uncoiled fifths are obscured.
        if(stacked&&(view==VisualizationViews.View.Torus||view==VisualizationViews.View.TorusLyrics||view==VisualizationViews.View.Overview))
        {
            float maxImageWidth=Mathf.Clamp(width*.2f,150,230),maxImageHeight=Mathf.Clamp(height*.115f,110,220);
            float cornerImageWidth=maxImageWidth,cornerImageHeight=cornerImageWidth/aspect;
            if(cornerImageHeight>maxImageHeight){cornerImageHeight=maxImageHeight;cornerImageWidth=cornerImageHeight*aspect;}
            float cornerCardWidth=Mathf.Max(cornerImageWidth+16,Mathf.Min(210,maxImageWidth+16));
            picture.style.width=cornerImageWidth;picture.style.height=cornerImageHeight;card.style.width=cornerCardWidth;
            float cornerRight=width-dock-cornerCardWidth-20;
            float cornerX=placement=="right"?cornerRight:placement=="center"?(width-cornerCardWidth)*.5f:Mathf.Max(12,left);
            card.style.left=Mathf.Clamp(cornerX,8,Mathf.Max(8,width-cornerCardWidth-8));card.style.top=Top;
            return;
        }
        bool lyricLayout=view==VisualizationViews.View.Overview||view==VisualizationViews.View.TorusLyrics;
        bool landscapeOverview=!stacked&&lyricLayout&&strip.width<width*.5f&&strip.height>0;
        bool aboveStrip=strip.height>0&&(view==VisualizationViews.View.Lyrics||(lyricLayout&&!landscapeOverview));
        float maxWidth=Mathf.Clamp(Mathf.Min(width*.26f,height*.42f),200,400),maxHeight=height*.44f;
        if(landscapeOverview&&placement==""){maxWidth=Mathf.Max(170,strip.width*1.3f);maxHeight=strip.yMin-Top-Chrome-8;}
        // The strip's right end carries only the incoming groove, so the card may reach its midline.
        else if(aboveStrip){maxHeight=strip.center.y-Top-Chrome;}
        // Stacked (Vertical, or a portrait window): mid left, small, clear of the torus above.
        if(stacked){maxWidth=Mathf.Clamp(width*.26f,150,320);maxHeight=height*.2f;}
        maxHeight=Mathf.Max(90,maxHeight);
        float imageWidth=maxWidth,imageHeight=imageWidth/aspect;
        if(imageHeight>maxHeight){imageHeight=maxHeight;imageWidth=imageHeight*aspect;}
        // A tall picture keeps a card wide enough for its caption to stay short; the picture centres in it.
        float cardWidth=Mathf.Max(imageWidth+16,MinCardWidth),right=width-dock-cardWidth-28;
        picture.style.width=imageWidth;picture.style.height=imageHeight;card.style.width=cardWidth;
        float x=placement=="left"?left:placement=="center"?(width-cardWidth)*.5f:placement==""&&landscapeOverview?strip.center.x-cardWidth*.5f:right;
        // At the left the story guide's label sits near the top; the card goes below it.
        bool guide=cue!=null&&!string.IsNullOrEmpty(cue.annotationTarget)&&cue.annotationTarget!="none";
        // Stacked: in the black space at the mid left, its foot just above the lyric strip.
        if(stacked){float cardHeight=imageHeight+Chrome,foot=strip.height>0?strip.yMin-10:height*.5f+cardHeight*.5f;
            card.style.left=Mathf.Max(12,left);card.style.top=Mathf.Max(Top,foot-cardHeight);return;}
        card.style.left=Mathf.Clamp(x,8,Mathf.Max(8,width-cardWidth-8));card.style.top=placement=="left"&&guide?170:Top;
    }
    void OnDestroy(){Clear();}
}
