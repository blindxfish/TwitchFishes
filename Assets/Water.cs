using System.Collections.Generic;
using UnityEngine;

public class Water : MonoBehaviour
{
    [SerializeField]
    public static List<GameObject> foodObjects = new List<GameObject>(); // Public static list accessed by the fish

    void OnTriggerEnter2D(Collider2D other)
    {
        if (other.gameObject.CompareTag("FishFood"))
        {
          
                foodObjects.Add(other.gameObject);
                Rigidbody2D rb = other.GetComponent<Rigidbody2D>();
                rb.gravityScale = 0;
                rb.velocity = Vector2.zero;
                rb.angularVelocity = 0;
          
        }
    }


    void OnTriggerExit2D(Collider2D other)
    {
        if (other.gameObject.CompareTag("FishFood"))
        {
            foodObjects.Remove(other.gameObject);
        }
    }
}
