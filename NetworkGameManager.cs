using System;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

public class NetworkGameManager : NetworkBehaviour
{
    public static NetworkGameManager instance;
    public const int MAX_PLAYERS = 8;
    public GameObject playerPrefab;

    public NetworkList<NWPlayerData> playerDataNetworkList;
    public delegate void OnPlayerDataListChanged();
    public static OnPlayerDataListChanged onPlayerDataListChanged;

    public GameObject myPlayer; // only set when ingame;

    string username;
    public enum GameState // only allow players to join while waiting to start
    {
        WaitingToStart,
        InHub,
        InGame,
        End
    }
    public NetworkVariable<GameState> gameState = new NetworkVariable<GameState>(GameState.WaitingToStart);

    private void Awake()
    {
        if (NetworkGameManager.instance == null)
        {
            instance = this;
        }
        else
        {
            Destroy(this.gameObject);
        }
        playerDataNetworkList = new NetworkList<NWPlayerData>();
        DontDestroyOnLoad(gameObject);

        username = PlayerPrefs.GetString("USERNAME", "Guest: " + UnityEngine.Random.Range(100, 1000));
    }

    private void Start()
    {
        playerDataNetworkList.OnListChanged += PlayerDataNetworkList_OnListChanged;
    }

    public string GetUsername()
    {
        return username;
    }

    public void SetUsername(string _username)
    {
        if (string.IsNullOrWhiteSpace(_username))
        {
            username = "Guest: " + UnityEngine.Random.Range(100, 1000);
        }
        else
        {
            username = _username;
        }

        PlayerPrefs.SetString("USERNAME", username);
    }

    public string GetUsernameFromClientId(ulong _clientId)
    {
        foreach (NWPlayerData playerData in playerDataNetworkList)
        {
            if (playerData.clientId == _clientId)
                return playerData.username.ToString();
        }
        return default;
    }

    private void PlayerDataNetworkList_OnListChanged(NetworkListEvent<NWPlayerData> changeEvent)
    {
        onPlayerDataListChanged?.Invoke();
    }

    public bool IsPlayerIndexConnected(int playerIndex)
    {
        return playerIndex < playerDataNetworkList.Count;
    }

    public NWPlayerData GetPlayerDataFromIndex(int _playerIndex)
    {
        return playerDataNetworkList[_playerIndex];
    }

    public NWPlayerData GetPlayerDataFromClientId(ulong clientId)
    {
        foreach (NWPlayerData playerData in playerDataNetworkList)
        {
            if (playerData.clientId == clientId)
                return playerData;
        }
        return default;
    }

    public NWPlayerData GetLocalPlayerData()
    {
        return GetPlayerDataFromClientId(NetworkManager.Singleton.LocalClientId);
    }

    public int GetPlayerDataIndexFromClientID(ulong clientId)
    {
        for (int i = 0; i < playerDataNetworkList.Count; i++)
        {
            if (playerDataNetworkList[i].clientId == clientId)
                return i;
        }
        return -1;
    }

    [ServerRpc(RequireOwnership = false)]
    void ChangePlayerSkinServerRpc(int skinIndex, ServerRpcParams rpcParams = default)
    {
        int playerIndex = GetPlayerDataIndexFromClientID(rpcParams.Receive.SenderClientId);
        NWPlayerData data = playerDataNetworkList[playerIndex];
        playerDataNetworkList[playerIndex] = data;
    }

    public void StartHost()
    {
        NetworkManager.Singleton.ConnectionApprovalCallback += Network_ConnectionApprovalCallback;
        NetworkManager.Singleton.OnClientConnectedCallback += Network_Server_OnClientConnectedCallback;
        NetworkManager.Singleton.OnClientDisconnectCallback += Network_Server_OnClientDisconnectCallback;

        NetworkManager.Singleton.StartHost();
        LoadLobbyJoinedScene();
    }

    private void Network_Server_OnClientDisconnectCallback(ulong _clientId)
    {
        for (int i = 0; i < playerDataNetworkList.Count; i++)
        {
            NWPlayerData data = playerDataNetworkList[i];
            if (data.clientId == _clientId)
            {
                playerDataNetworkList.RemoveAt(i);
            }
        }
    }

    private void Network_Server_OnClientConnectedCallback(ulong _clientId)
    {
        playerDataNetworkList.Add(new NWPlayerData
        {
            clientId = _clientId,
            username = GetUsername() // Ajoutez cette ligne pour définir le nom d'utilisateur
        });
        SetUsernameServerRpc(GetUsername());
    }

    public void StartClient()
    {
        NetworkManager.Singleton.OnClientDisconnectCallback += Network_OnClientDisconnectCallback;
        NetworkManager.Singleton.OnClientConnectedCallback += Network_Client_OnClientConnectedCallback;

        NetworkManager.Singleton.StartClient();
    }

    private void Network_Client_OnClientConnectedCallback(ulong obj)
    {
        SetUsernameServerRpc(GetUsername());
    }

    [ServerRpc(RequireOwnership = false)]
    void SetUsernameServerRpc(string _username, ServerRpcParams rpcParams = default)
    {
        int playerIndex = GetPlayerDataIndexFromClientID(rpcParams.Receive.SenderClientId);
        NWPlayerData data = playerDataNetworkList[playerIndex];
        data.username = _username;
        playerDataNetworkList[playerIndex] = data;
    }

    private void Network_OnClientDisconnectCallback(ulong clientId)
    {
        if (SceneManager.GetSceneByName("Lobbies") == SceneManager.GetActiveScene())
        {
            // failed to connect
            FindObjectOfType<LobbyBrowseUI>().ConnectionFailed();
        }
        else if (SceneManager.GetSceneByName("WaitRoom") == SceneManager.GetActiveScene())
        {
            // inside a lobby;
            FindObjectOfType<LobbyJoinedUI>().LeaveLobbyPressed();
        }
        else
        {
            // ingame
            //UI.instance.EnableHostDisconnectTab();
        }
    }

    void Network_ConnectionApprovalCallback(NetworkManager.ConnectionApprovalRequest connectionApprovalRequest, NetworkManager.ConnectionApprovalResponse connectionApprovalResponse)
    {
        if (gameState.Value != GameState.WaitingToStart)
        {
            connectionApprovalResponse.Approved = false;
            connectionApprovalResponse.Reason = "Game has already started.";
            return;
        }
        if (NetworkManager.Singleton.ConnectedClientsIds.Count >= MAX_PLAYERS)
        {
            connectionApprovalResponse.Approved = false;
            connectionApprovalResponse.Reason = "Game is full.";
            return;
        }
        connectionApprovalResponse.Approved = true;
        //connectionApprovalResponse.CreatePlayerObject = true; 
    }

    void LoadLobbyJoinedScene()
    {
        NetworkManager.Singleton.SceneManager.LoadScene("WaitRoom", LoadSceneMode.Single);
    }

    public void LoadGameScene()
    {
        LobbyManager.instance.DeleteLobby();
        NetworkManager.Singleton.SceneManager.LoadScene("SampleScene", LoadSceneMode.Single);
    }

    public void SpawnPlayers() // server
    {
        foreach (ulong clientId in NetworkManager.Singleton.ConnectedClientsIds)
        {
            Vector3 spawnPosition = GetRandomSpawnPosition();
            GameObject player = Instantiate(playerPrefab, spawnPosition, Quaternion.identity);
            player.GetComponent<NetworkObject>().SpawnAsPlayerObject(clientId, true);
        }
    }

    private Vector3 GetRandomSpawnPosition()
    {
        Vector3 center = Vector3.zero; // Remplacer par le centre de votre zone de spawn
        Vector3 size = new Vector3(20, 0, 20); // Remplacer par la taille de votre zone de spawn

        float randomX = UnityEngine.Random.Range(center.x - size.x / 2, center.x + size.x / 2);
        float randomZ = UnityEngine.Random.Range(center.z - size.z / 2, center.z + size.z / 2);

        return new Vector3(randomX, 0.1f, randomZ); // Ajuster Y si nécessaire
    }
}
