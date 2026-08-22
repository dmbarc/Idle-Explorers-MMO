using UnityEngine;
using UnityEngine.AI;

public class MonsterSpawner : MonoBehaviour
{
    public GameObject prefabToSpawn;
    public float minXSpawn = 400f;
    public float maxXSpawn = 500f;
    public float minZSpawn = 400f;
    public float maxZSpawn = 500f;
    public float spawnInterval = 5f;
    public int maxMonsterCount = 100;

    private int currentMonsterCount = 0;
    private float timer = 0f;

    void Update()
    {
        timer += Time.deltaTime;
        if (timer >= spawnInterval && currentMonsterCount < maxMonsterCount)
        {
            SpawnMonster();
            timer = 0f;
        }
    }

    void SpawnMonster()
    {
        Vector3 randomPos = new Vector3(Random.Range(minXSpawn, maxXSpawn), 10f, Random.Range(minZSpawn, maxZSpawn));

        if (NavMesh.SamplePosition(randomPos, out NavMeshHit hit, 20f, NavMesh.AllAreas))
        {
            GameObject monster = Instantiate(prefabToSpawn, hit.position, Quaternion.identity);
            currentMonsterCount++;

            // Tell the monster who to notify when it dies
            MonsterController mc = monster.GetComponent<MonsterController>();
            if (mc != null) mc.SetSpawner(this);
        }
    }

    // Called by dying monsters
    public void MonsterDied()
    {
        currentMonsterCount = Mathf.Max(0, currentMonsterCount - 1);
    }
}