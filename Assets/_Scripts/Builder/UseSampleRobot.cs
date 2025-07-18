using UnityEngine;

public class UseSampleRobot : MonoBehaviour
{
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    [SerializeField] private GameObject sampleRobot;
    [SerializeField] private GameObject root;

    public void LoadRobot()
    {
        foreach (Transform t in root.transform)
        {
            if (t.gameObject.name.Contains( "Robot"))
            Destroy(t.gameObject);
        }

        GameObject go = Instantiate(sampleRobot,root.transform, root.transform);
        CameraControl.Instance.parentModel = go;
    }
}
