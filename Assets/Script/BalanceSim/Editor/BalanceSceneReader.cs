using System;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

// MainScene을 한 번만 열어 여러 읽기 작업에 넘겨주고 바로 닫는다.
// 이미 에디터에 열려 있으면 그 씬을 그대로 읽고 닫지 않는다. 씬은 수정·저장하지 않는다.
public static class BalanceSceneReader
{
    private const string MainScenePath = "Assets/MainScene/MainScene.unity";
    private const string OpenSceneNote = "열려 있는 MainScene에서 읽음";
    private const string PreviewSceneNote = "MainScene(미리보기)에서 읽음";

    // read에 씬을 넘기고, 어디서 읽었는지 표기 문구를 돌려준다.
    public static string Read(Action<Scene> read)
    {
        Scene openScene = SceneManager.GetSceneByPath(MainScenePath);
        if (openScene.IsValid() && openScene.isLoaded)
        {
            read(openScene);
            return OpenSceneNote;
        }

        Scene preview = EditorSceneManager.OpenPreviewScene(MainScenePath);
        try
        {
            read(preview);
            return PreviewSceneNote;
        }
        finally
        {
            EditorSceneManager.ClosePreviewScene(preview);
        }
    }
}
