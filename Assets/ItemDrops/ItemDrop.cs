using UnityEngine;

[CreateAssetMenu(menuName = "Drop/Item")]
public class ItemDrop : ScriptableObject
{
    public string itemName;
    public Sprite icon;          
    public int maxStack = 99;
    public bool isConsumable = false;
    public string description = "";
    // Add more later (value, crafting recipe, etc.)
}