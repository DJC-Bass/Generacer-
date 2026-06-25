using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// One row in the car-selection list. Reports hover (mouse) and focus (gamepad/keyboard
/// navigation) to the <see cref="CarSelectionController"/> so it shows that car's rotating
/// preview. The row's Button.onClick (wired by the controller) handles actual selection.
/// </summary>
public class CarListEntry : MonoBehaviour, IPointerEnterHandler, ISelectHandler
{
    private CarSelectionController controller;
    private int index;

    public void Init(CarSelectionController c, int i)
    {
        controller = c;
        index = i;
    }

    public void OnPointerEnter(PointerEventData eventData)
    {
        if (controller != null) controller.SelectCar(index);
    }

    public void OnSelect(BaseEventData eventData)
    {
        if (controller != null) controller.SelectCar(index);
    }
}
