using System;
using System.Collections;
using UnityEngine;

// Floating help text next to each VR controller.
//
// The SteamVR Interaction System used to draw these hints on the controller render
// models.  Now a small text label floats just above each controller model (see
// ControllerModel.cs), which works the same on every runtime.  Right A (or menu)
// toggles them, and the choice is saved.

public class ControllerHints : MonoBehaviour
{
    public Transform leftHand;
    public Transform rightHand;

    private GameObject leftHint;
    private GameObject rightHint;
    private bool showing;

    private IEnumerator Start()
    {
        KatangaInput.Enable();

        // Wait a frame so LaunchAndPlay has parsed the command line and set slideshowMode.
        yield return null;

        leftHint = CreateLabel(leftHand, LeftText());
        rightHint = CreateLabel(rightHand, RightText());

        showing = Convert.ToBoolean(PlayerPrefs.GetInt("hints", 1));
        UpdateHints();
    }

    private void Update()
    {
        if (leftHint == null && rightHint == null)
            return;

        if (KatangaInput.ToggleHints.WasPressedThisFrame())
        {
            showing = !showing;
            PlayerPrefs.SetInt("hints", Convert.ToInt32(showing));
            UpdateHints();
        }
    }

    private void UpdateHints()
    {
        if (leftHint != null)
            leftHint.SetActive(showing);
        if (rightHint != null)
            rightHint.SetActive(showing);
    }

    private static string LeftText()
    {
        string text = "Stick up/down: Screen higher/lower\n" +
                      "Stick left/right: Screen smaller/bigger\n" +
                      "Trigger: Cycle environment\n" +
                      "X/A or Menu: Sharpen / FSR upscale";
        if (Game.slideshowMode)
            text += "\nGrip: Next slide";
        return text;
    }

    private static string RightText()
    {
        string text = "Stick up/down: Screen farther/nearer\n" +
                      "Stick left/right: Flatten/curve screen\n" +
                      "Trigger: Recenter screen\n" +
                      "A or Menu: Show/hide help";
        if (Game.slideshowMode)
            text += "\nGrip (hold): Pause slideshow";
        return text;
    }

    // Small billboard-less TextMesh sitting just above the controller, tilted toward
    // the user.  TextMesh keeps this independent of any UI canvas setup.

    private static GameObject CreateLabel(Transform hand, string text)
    {
        if (hand == null)
            return null;

        GameObject label = new GameObject("Hint");
        label.transform.SetParent(hand, false);
        label.transform.localPosition = new Vector3(0.0f, 0.09f, 0.02f);
        label.transform.localRotation = Quaternion.Euler(45.0f, 0.0f, 0.0f);
        label.transform.localScale = Vector3.one * 0.006f;

        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        TextMesh mesh = label.AddComponent<TextMesh>();
        mesh.text = text;
        mesh.font = font;
        mesh.fontSize = 48;
        mesh.characterSize = 0.25f;
        mesh.anchor = TextAnchor.LowerCenter;
        mesh.alignment = TextAlignment.Left;
        mesh.color = new Color(0.9f, 0.9f, 0.9f, 1.0f);

        label.GetComponent<MeshRenderer>().sharedMaterial = font.material;

        return label;
    }
}
