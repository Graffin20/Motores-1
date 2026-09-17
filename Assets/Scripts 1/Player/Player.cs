using UnityEngine;

public class Player : MonoBehaviour
{

    public float velocidad = 5.0f;

    [SerializeField] private InteractComponent interactComponent;
    void Update()
    {
        Vector3 movimiento = Vector3.zero;

        if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow))
            movimiento += transform.forward;
        if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow))
            movimiento -= transform.forward;
        if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow))
            movimiento -= transform.right;
        if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow))
            movimiento += transform.right;

        if (Input.GetKeyDown(KeyCode.E))
        {
            interactComponent.Interact();
        }

            if (movimiento != Vector3.zero)
        {   
            movimiento.Normalize();
            transform.position += movimiento * velocidad * Time.deltaTime;
        }

    }
}