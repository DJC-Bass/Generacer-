using UnityEngine;
using UnityEngine.SceneManagement;

public class ReturnToHub : MonoBehaviour
{
    public int hubSceneIndex = 0;
    public KeyCode returnKey = KeyCode.Escape;

    void Update()
    {
        if (GameLoopManager.Instance != null)
            GameLoopManager.Instance.NotifyReturnedToHub();

        if (Input.GetKeyDown(returnKey))
            SceneManager.LoadScene(hubSceneIndex);
    }
}