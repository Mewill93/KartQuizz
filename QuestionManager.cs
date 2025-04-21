using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using Unity.Collections;
using PUROPORO;

public class QuestionManager : NetworkBehaviour
{
    public List<Question> questions;
    public QuestionCanvas questionCanvas;
    public GameObject answerPrefab;
    public Transform spawnArea;
    public float spawnRadius = 5f;
    public float minTimeBetweenQuestions = 5f;
    public float maxTimeBetweenQuestions = 10f;
    public float questionDisplayTime = 30f;
    public Color[] answerColorsArray = { Color.blue, Color.green, Color.red, Color.yellow };

    private NetworkQuestion currentQuestion;
    private bool questionActive = false;
    private List<GameObject> activeAnswerObjects = new List<GameObject>();
    private Dictionary<ulong, bool> playerAnswerStates = new Dictionary<ulong, bool>();
    private int score = 0;

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            StartCoroutine(QuestionRoutine());
        }
    }

    private IEnumerator QuestionRoutine()
    {
        while (questions.Exists(q => !q.hasBeenAsked)) // Continue tant qu'il y a des questions non posées
        {
            float timeToNextQuestion = Random.Range(minTimeBetweenQuestions, maxTimeBetweenQuestions);
            yield return new WaitForSeconds(timeToNextQuestion);

            if (IsServer)
            {
                ShowRandomQuestion();
                questionActive = true;

                float timeLeft = questionDisplayTime;
                while (timeLeft > 0)
                {
                    UpdateTimerClientRpc(timeLeft);
                    yield return new WaitForSeconds(1f);
                    timeLeft -= 1f;
                }

                if (questionActive)
                {
                    EvaluateAnswers();
                    questionActive = false;
                    ClearAnswers();
                    ClearQuestionClientRpc();
                }
            }
        }

        EndGameForAllPlayers();
    }

    private void ShowRandomQuestion()
    {
        var question = GetRandomQuestion();
        currentQuestion = NetworkQuestion.FromQuestion(question);

        // Générer des positions et des couleurs des réponses sur le serveur et les envoyer aux clients
        List<Vector3> answerPositions = GenerateAnswerPositions(question.answers.Length);
        List<Color> answerColors = GenerateAnswerColors(question.answers.Length);

        ShowRandomQuestionClientRpc(currentQuestion, answerPositions.ToArray(), answerColors.ToArray());
        DisplayAnswersRandomly(currentQuestion, answerPositions, answerColors);

        question.hasBeenAsked = true; // Marquer la question comme posée
    }

    [ClientRpc]
    private void ShowRandomQuestionClientRpc(NetworkQuestion netQuestion, Vector3[] positions, Color[] colors)
    {
        currentQuestion = netQuestion;
        questionCanvas.DisplayQuestion(currentQuestion, colors);
        DisplayAnswersRandomly(currentQuestion, new List<Vector3>(positions), new List<Color>(colors));
    }

    [ClientRpc]
    private void UpdateTimerClientRpc(float timeLeft)
    {
        questionCanvas.UpdateTimer(timeLeft);
    }

    [ClientRpc]
    private void ClearQuestionClientRpc()
    {
        questionCanvas.ClearQuestion();
        ClearAnswers();
    }

    [ClientRpc]
    private void EndGameClientRpc(int finalScore)
    {
        questionCanvas.DisplayEndGameMessage(finalScore);
    }

    private Question GetRandomQuestion()
    {
        List<Question> availableQuestions = questions.FindAll(q => !q.hasBeenAsked);
        int index = Random.Range(0, availableQuestions.Count);
        return availableQuestions[index];
    }

    private List<Vector3> GenerateAnswerPositions(int count)
    {
        List<Vector3> positions = new List<Vector3>();
        List<Vector3> usedPositions = new List<Vector3>();

        for (int i = 0; i < count; i++)
        {
            Vector3 randomPosition;
            int attempts = 0;  // Limite pour éviter les boucles infinies
            do
            {
                randomPosition = GetRandomPosition(); // Générer une position aléatoire
                attempts++;
            } while (IsPositionTooCloseToOthers(randomPosition, usedPositions) && attempts < 100); // S'assurer que la nouvelle position n'est pas trop proche des autres

            usedPositions.Add(randomPosition);
            positions.Add(randomPosition);
        }

        // S'assurer que nous avons exactement 4 positions
        while (positions.Count < 4)
        {
            positions.Add(Vector3.zero);
        }

        return positions;
    }

    private bool IsPositionTooCloseToOthers(Vector3 position, List<Vector3> existingPositions)
    {
        foreach (Vector3 existingPosition in existingPositions)
        {
            if (Vector3.Distance(position, existingPosition) < spawnRadius)
            {
                return true;  // La nouvelle position est trop proche d'une position existante
            }
        }
        return false; // La position est suffisamment éloignée
    }

    private List<Color> GenerateAnswerColors(int count)
    {
        List<Color> availableColors = new List<Color>(answerColorsArray);
        List<Color> selectedColors = new List<Color>();

        for (int i = 0; i < count; i++)
        {
            int colorIndex = Random.Range(0, availableColors.Count);
            selectedColors.Add(availableColors[colorIndex]);
            availableColors.RemoveAt(colorIndex);
        }

        // S'assurer que nous avons exactement 4 couleurs
        while (selectedColors.Count < 4)
        {
            selectedColors.Add(Color.clear);
        }

        return selectedColors;
    }

    private void DisplayAnswersRandomly(NetworkQuestion question, List<Vector3> positions, List<Color> colors)
    {
        ClearAnswers(); // Effacer les réponses existantes avant d'afficher les nouvelles

        FixedString128Bytes[] answers = new FixedString128Bytes[] { question.answer1, question.answer2, question.answer3, question.answer4 };

        // Assurez-vous que le nombre de positions et de couleurs correspond au nombre de réponses
        if (answers.Length != 4 || positions.Count != 4 || colors.Count != 4)
        {
            Debug.LogError("Le nombre de réponses, de positions ou de couleurs ne correspond pas à 4.");
            return;
        }

        for (int i = 0; i < answers.Length; i++)
        {
            GameObject answerObj = Instantiate(answerPrefab, positions[i], Quaternion.identity);
            Renderer renderer = answerObj.GetComponent<Renderer>();

            // Assign the color to the answer
            Color answerColor = colors[i];

            if (renderer != null)
            {
                renderer.material.color = answerColor;
            }

            answerObj.GetComponent<AnswerPoint>().Setup(answers[i].ToString(), i, this, answerColor);
            activeAnswerObjects.Add(answerObj);
        }

        // Update the answer indicator for all clients
        UpdateAnswerIndicatorClientRpc(positions.ToArray(), colors.ToArray());
    }

    [ClientRpc]
    private void UpdateAnswerIndicatorClientRpc(Vector3[] positions, Color[] colors)
    {
        // Find the local player and update their answer indicator spheres
        var localPlayer = NetworkManager.Singleton.LocalClient.PlayerObject;
        var indicator = localPlayer.GetComponent<AnswerIndicator>();
        if (indicator != null)
        {
            indicator.SetAnswerPositionsAndColors(positions, colors);
            indicator.SetSpheresVisible(true);
        }
    }

    private Vector3 GetRandomPosition()
    {
        Vector3 center = spawnArea.position;
        Vector3 size = spawnArea.localScale * 10f - new Vector3(5f, 0, 5f); // Réduire la taille de la zone de spawn

        float randomX = Random.Range(center.x - size.x / 2, center.x + size.x / 2);
        float randomZ = Random.Range(center.z - size.z / 2, center.z + size.z / 2);

        return new Vector3(randomX, 0.11f, randomZ); // Ajuster la position Y si nécessaire
    }

    public void UpdatePlayerAnswerState(ulong playerId, bool isCorrect)
    {
        if (playerAnswerStates.ContainsKey(playerId))
        {
            playerAnswerStates[playerId] = isCorrect;
        }
        else
        {
            playerAnswerStates.Add(playerId, isCorrect);
        }
    }

    public bool IsCorrectAnswer(int answerIndex)
    {
        return currentQuestion.correctAnswerIndex == answerIndex;
    }

    private void EvaluateAnswers()
    {
        foreach (var playerAnswerState in playerAnswerStates)
        {
            if (playerAnswerState.Value)
            {
                var player = NetworkManager.Singleton.ConnectedClients[playerAnswerState.Key].PlayerObject;
                var carController = player.GetComponent<GoKartController>();
                carController.AddScore(100);
            }
        }
        playerAnswerStates.Clear();
    }

    private void ClearAnswers()
    {
        foreach (GameObject answerObj in activeAnswerObjects)
        {
            if (answerObj != null)
            {
                Destroy(answerObj);
            }
        }
        activeAnswerObjects.Clear();
        // Hide the answer indicator spheres for all clients
        HideAnswerIndicatorClientRpc();
    }

    [ClientRpc]
    private void HideAnswerIndicatorClientRpc()
    {
        var players = GameObject.FindGameObjectsWithTag("Player");
        foreach (var player in players)
        {
            var indicator = player.GetComponent<AnswerIndicator>();
            if (indicator != null)
            {
                indicator.SetSpheresVisible(false);
            }
        }
    }

    private void EndGameForAllPlayers()
    {
        var players = GameObject.FindGameObjectsWithTag("Player");
        foreach (var player in players)
        {
            var carController = player.GetComponent<GoKartController>();
            if (carController != null)
            {
                EndGameClientRpc(carController.GetScore());
            }
        }
    }
}
