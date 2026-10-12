using UnityEngine;

public sealed class TrolaBannerSway : MonoBehaviour
{
    public float phase;
    private Quaternion rest;
    private Mesh original, cloth;
    private Vector3[] vertices, moved;
    private float top, height;

    private void Awake()
    {
        rest = transform.localRotation;
        var filter = GetComponent<MeshFilter>();
        if (filter == null || filter.sharedMesh == null || !filter.sharedMesh.isReadable) return;
        original = filter.sharedMesh;
        cloth = Instantiate(original); cloth.name = original.name + " runtime wind";
        filter.sharedMesh = cloth; vertices = original.vertices; moved = new Vector3[vertices.Length];
        top = original.bounds.max.y; height = Mathf.Max(.01f, original.bounds.size.y);
    }

    private void Update()
    {
        float t = Time.unscaledTime;
        if (cloth == null) { transform.localRotation = rest * Quaternion.Euler(.9f * Mathf.Sin(t * .73f + phase), 0, 1.7f * Mathf.Sin(t * .57f + phase)); return; }
        for (int i = 0; i < vertices.Length; i++)
        {
            float free = Mathf.Pow(Mathf.Clamp01((top - vertices[i].y) / height), 1.6f);
            moved[i] = vertices[i] + new Vector3(.035f * free * Mathf.Sin(t * 1.1f + vertices[i].y * 2 + phase), 0, .09f * free * Mathf.Sin(t * 1.45f + vertices[i].x * 2.5f + phase));
        }
        cloth.vertices = moved; cloth.RecalculateNormals(); cloth.RecalculateBounds();
    }

    private void OnDestroy()
    {
        if (cloth == null) return;
        var filter = GetComponent<MeshFilter>(); if (filter != null) filter.sharedMesh = original;
        Destroy(cloth);
    }
}
