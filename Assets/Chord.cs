using System.Collections.Generic;
using UnityEngine;

public class Chord : MonoBehaviour
{
    public Note Note1;
    public Note Note2;
    LineRenderer LineRenderer;
    
    public void Init(Note note1, Note note2, LineRenderer lineRenderer)
    {
        Note1 = note1;
        Note2 = note2;
        LineRenderer = lineRenderer;
    }

    public void Line()
    {
        LineRenderer.positionCount = 2;
        LineRenderer.SetPosition(0,Note1.transform.position);
        LineRenderer.SetPosition(1, Note2.transform.localPosition);
    }

    public void Fifth(List<Vector3> fifthLine)
    {
        LineRenderer.positionCount = fifthLine.Count;
        LineRenderer.SetPositions(fifthLine.ToArray());
    }
}