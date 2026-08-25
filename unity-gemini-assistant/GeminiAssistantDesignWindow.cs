using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace GeminiDesignTool
{
    [Serializable]
    public class GeminiGenerateRequest
    {
        public SystemInstructionData system_instruction;
        public RequestContentData[] contents;
    }

    [Serializable]
    public class SystemInstructionData { public RequestPartData[] parts; }

    // GCP 인증용 클래스 추가 및 400 에러 방지용 role 속성 추가
    [Serializable]
    public class GCPServiceAccount
    {
        public string project_id;
        public string private_key;
        public string client_email;
    }

    [Serializable]
    public class GCPTokenResponse
    {
        public string access_token;
        public int expires_in;
        public string token_type;
    }

    [Serializable]
    public class RequestContentData
    {
        public string role;
        public RequestPartData[] parts;
    }

    [Serializable]
    public class RequestPartData { public string text; }

    public class BypassCertificate : CertificateHandler
    {
        protected override bool ValidateCertificate(byte[] certificateData) => true;
    }

    public class GeminiAssistantDesignWindow : EditorWindow
    {
        private string jsonFilePath = "";
        private string gcpRegion = "us-central1";
        private string systemPrompt = "";
        private string userPrompt = "";
        private string responseText = "";

        private Vector2 scrollPos;
        private Vector2 systemPromptScrollPos;
        private Vector2 userPromptScrollPos;

        private int selectedModelIndex = 0;
        // 에디터 창에 보여질 깔끔한 이름들
        private readonly string[] modelDisplayNames = new string[] { "Gemini 2.5 Flash", "Gemini 3.0 Flash", "Gemini 3.1 Pro" };

        // 구글 서버가 인식하는 정확한 시스템 엔드포인트 이름
        private readonly string[] modelEndpoints = new string[] { "gemini-2.5-flash", "gemini-3-flash-preview", "gemini-3.1-pro-preview" };
        private List<UnityEngine.Object> targetObjects = new List<UnityEngine.Object>() { null };

        private string logDirectoryPath;
        private string settingsFilePath;
        private bool isGenerating = false;

        [MenuItem("Tools/Gemini Assistant Design (GAD)")]
        public static void ShowWindow()
        {
            GetWindow<GeminiAssistantDesignWindow>("Gemini Design");
        }

        private void OnEnable()
        {
            logDirectoryPath = Path.Combine(Application.dataPath, "GeminiLogs");
            settingsFilePath = Path.Combine(Application.dataPath, "../Gemini_Settings_GAD.txt");
            LoadSettingsFromFile();
        }

        private void OnGUI()
        {
            GUILayout.Label("Vertex AI 인증 및 설정 (GCP Settings)", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            jsonFilePath = EditorGUILayout.TextField("GCP JSON Key", jsonFilePath);
            if (GUILayout.Button("찾기", GUILayout.Width(50)))
            {
                string path = EditorUtility.OpenFilePanel("Select GCP Service Account JSON", "", "json");
                if (!string.IsNullOrEmpty(path)) jsonFilePath = path;
            }
            EditorGUILayout.EndHorizontal();

            gcpRegion = EditorGUILayout.TextField("GCP Region", gcpRegion);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("설정 텍스트 파일에 저장", GUILayout.Width(150))) SaveSettingsToFile();
            EditorGUILayout.EndHorizontal();

            GUILayout.Space(10);

            GUILayout.Label("System Prompt (역할 및 명령어 규칙)", EditorStyles.boldLabel);
            systemPromptScrollPos = EditorGUILayout.BeginScrollView(systemPromptScrollPos, GUILayout.Height(100));
            systemPrompt = EditorGUILayout.TextArea(systemPrompt, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();

            GUILayout.Space(10);
            GUILayout.Label("오브젝트 및 에셋 할당 (Scene GameObject / Project Asset)", EditorStyles.boldLabel);

            for (int i = 0; i < targetObjects.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                targetObjects[i] = EditorGUILayout.ObjectField($"Object {i + 1}", targetObjects[i], typeof(UnityEngine.Object), true);

                if (GUILayout.Button("X", GUILayout.Width(30)))
                {
                    targetObjects.RemoveAt(i);
                    i--;
                }
                EditorGUILayout.EndHorizontal();
            }

            if (GUILayout.Button("+ 오브젝트 슬롯 추가")) targetObjects.Add(null);

            GUILayout.Space(10);
            GUILayout.Label("사용자 명령 (User Prompt)", EditorStyles.boldLabel);
            userPromptScrollPos = EditorGUILayout.BeginScrollView(userPromptScrollPos, GUILayout.Height(80));
            userPrompt = EditorGUILayout.TextArea(userPrompt, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();

            GUILayout.Space(10);
            GUILayout.Label("모델 선택 (Model)", EditorStyles.boldLabel);
            selectedModelIndex = EditorGUILayout.Popup(selectedModelIndex, modelDisplayNames);

            GUILayout.Space(10);

            EditorGUI.BeginDisabledGroup(isGenerating);
            string btnText = isGenerating ? "명령 분석 및 에디터 제어 중... 잠시 대기" : "명령 실행 (Execute)";
            if (GUILayout.Button(btnText, GUILayout.Height(40))) CallGeminiAPI();
            EditorGUI.EndDisabledGroup();

            GUILayout.Space(10);
            GUILayout.Label("결과 및 시스템 로그", EditorStyles.boldLabel);
            scrollPos = EditorGUILayout.BeginScrollView(scrollPos);
            responseText = EditorGUILayout.TextArea(responseText, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
        }

        private string ReadObjectData()
        {
            StringBuilder sb = new StringBuilder();

            sb.AppendLine("[현재 씬에 존재하는 주요 오브젝트 목록 및 인스펙터 상태]");
            // 씬의 최상위 오브젝트들을 순회하며 상태를 자세히 읽어옵니다.
            foreach (GameObject rootObj in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
            {
                sb.Append($"- {rootObj.name} ");

                // MonoBehaviour 컴포넌트들을 찾아서 주요 연결 상태를 추출
                MonoBehaviour[] scripts = rootObj.GetComponents<MonoBehaviour>();
                if (scripts.Length > 0)
                {
                    sb.Append("(");
                    foreach (var script in scripts)
                    {
                        if (script == null) continue;
                        Type type = script.GetType();
                        sb.Append($"[{type.Name}: ");

                        // 퍼블릭 변수들만 가볍게 스캔하여 어떤 오브젝트가 할당되어 있는지 확인
                        FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
                        foreach (var field in fields)
                        {
                            object value = field.GetValue(script);
                            if (value is UnityEngine.Object uObj && uObj != null)
                            {
                                sb.Append($"{field.Name}={uObj.name}, ");
                            }
                            else if (value == null || (value is UnityEngine.Object uObjNull && uObjNull == null))
                            {
                                sb.Append($"{field.Name}=Null, ");
                            }
                        }
                        sb.Append("] ");
                    }
                    sb.Append(")");
                }
                sb.AppendLine();
            }

            sb.AppendLine("\n[슬롯에 직접 할당된 타겟 오브젝트 정보]");
            int count = 0;
            foreach (var obj in targetObjects)
            {
                if (obj != null)
                {
                    count++;
                    sb.AppendLine($"[Object {count}] 이름: {obj.name}, 타입: {obj.GetType().Name}");
                }
            }

            return sb.ToString();
        }

        private static string Base64UrlEncode(byte[] input)
        {
            string output = Convert.ToBase64String(input);
            output = output.Split('=')[0]; output = output.Replace('+', '-'); output = output.Replace('/', '_');
            return output;
        }

        private async Task<string> GetOAuthTokenAsync(GCPServiceAccount account)
        {
            long iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long exp = iat + 3600;

            string header = "{\"alg\":\"RS256\",\"typ\":\"JWT\"}";
            string payload = $"{{\"iss\":\"{account.client_email}\",\"scope\":\"https://www.googleapis.com/auth/cloud-platform\",\"aud\":\"https://oauth2.googleapis.com/token\",\"exp\":{exp},\"iat\":{iat}}}";

            string base64Header = Base64UrlEncode(Encoding.UTF8.GetBytes(header));
            string base64Payload = Base64UrlEncode(Encoding.UTF8.GetBytes(payload));
            string unsignedJwt = $"{base64Header}.{base64Payload}";

            string privateKeyStr = account.private_key;
            privateKeyStr = privateKeyStr.Replace("-----BEGIN PRIVATE KEY-----", "").Replace("-----END PRIVATE KEY-----", "").Replace("\n", "").Replace("\r", "");
            byte[] privateKeyBytes = Convert.FromBase64String(privateKeyStr);

            using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider())
            {
                rsa.ImportParameters(ParseRSAPrivateKey(privateKeyBytes));
                byte[] signature;
                using (SHA256 sha256 = SHA256.Create())
                {
                    byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(unsignedJwt));
                    RSAPKCS1SignatureFormatter formatter = new RSAPKCS1SignatureFormatter(rsa);
                    formatter.SetHashAlgorithm("SHA256");
                    signature = formatter.CreateSignature(hash);
                }

                string signedJwt = $"{unsignedJwt}.{Base64UrlEncode(signature)}";
                WWWForm form = new WWWForm();
                form.AddField("grant_type", "urn:ietf:params:oauth:grant-type:jwt-bearer");
                form.AddField("assertion", signedJwt);

                using (UnityWebRequest webRequest = UnityWebRequest.Post("https://oauth2.googleapis.com/token", form))
                {
                    var operation = webRequest.SendWebRequest();
                    while (!operation.isDone) await Task.Yield();
                    if (webRequest.result == UnityWebRequest.Result.Success)
                    {
                        GCPTokenResponse tokenRes = JsonUtility.FromJson<GCPTokenResponse>(webRequest.downloadHandler.text);
                        return tokenRes.access_token;
                    }
                    else throw new Exception("OAuth 토큰 발급 실패: " + webRequest.downloadHandler.text);
                }
            }
        }

        private static RSAParameters ParseRSAPrivateKey(byte[] privKey)
        {
            using (MemoryStream mem = new MemoryStream(privKey))
            using (BinaryReader binr = new BinaryReader(mem))
            {
                if (binr.ReadByte() != 0x30) throw new Exception("Expected Sequence");
                DecodeDerLength(binr);
                if (binr.ReadByte() != 0x02) throw new Exception("Expected Version");
                int versionLen = DecodeDerLength(binr);
                binr.ReadBytes(versionLen);
                if (binr.ReadByte() != 0x30) throw new Exception("Expected AlgorithmIdentifier");
                int algLen = DecodeDerLength(binr);
                binr.ReadBytes(algLen);
                if (binr.ReadByte() != 0x04) throw new Exception("Expected Octet String");
                DecodeDerLength(binr);
                if (binr.ReadByte() != 0x30) throw new Exception("Expected RSAPrivateKey Sequence");
                DecodeDerLength(binr);
                if (binr.ReadByte() != 0x02) throw new Exception("Expected RSAPrivateKey Version");
                int pkcs1VersionLen = DecodeDerLength(binr);
                binr.ReadBytes(pkcs1VersionLen);

                RSAParameters parameters = new RSAParameters();
                parameters.Modulus = ReadDerInteger(binr); parameters.Exponent = ReadDerInteger(binr); parameters.D = ReadDerInteger(binr);
                parameters.P = ReadDerInteger(binr); parameters.Q = ReadDerInteger(binr); parameters.DP = ReadDerInteger(binr);
                parameters.DQ = ReadDerInteger(binr); parameters.InverseQ = ReadDerInteger(binr);
                return parameters;
            }
        }

        private static int DecodeDerLength(BinaryReader reader)
        {
            byte b = reader.ReadByte(); if ((b & 0x80) == 0) return b;
            int count = b & 0x7F; int len = 0;
            for (int i = 0; i < count; i++) len = (len << 8) | reader.ReadByte();
            return len;
        }

        private static byte[] ReadDerInteger(BinaryReader reader)
        {
            byte tag = reader.ReadByte(); if (tag != 0x02) throw new Exception("Expected Integer");
            int length = DecodeDerLength(reader); byte[] data = reader.ReadBytes(length);
            if (data.Length > 1 && data[0] == 0x00)
            {
                byte[] trimmed = new byte[data.Length - 1]; Buffer.BlockCopy(data, 1, trimmed, 0, trimmed.Length); return trimmed;
            }
            return data;
        }


        private async void CallGeminiAPI()
        {
            if (isGenerating) return;

            if (string.IsNullOrEmpty(jsonFilePath) || !File.Exists(jsonFilePath))
            {
                responseText = "GCP 서비스 계정 JSON 파일 경로가 올바르지 않습니다.";
                return;
            }

            isGenerating = true;
            responseText = $"{modelDisplayNames[selectedModelIndex]} (Vertex AI) 모델이 씬 상태를 분석하고 있습니다...\n(OAuth 2.0 토큰 발급 및 통신 중...)";
            Repaint();

            try
            {
                string jsonText = File.ReadAllText(jsonFilePath);
                GCPServiceAccount account = JsonUtility.FromJson<GCPServiceAccount>(jsonText);
                string accessToken = await GetOAuthTokenAsync(account);

                string objectContext = ReadObjectData();
                string finalUserPromptToSend = $"[씬 및 할당 오브젝트 상태]\n{objectContext}\n[사용자 명령]\n{userPrompt}";

                string selectedEndpoint = modelEndpoints[selectedModelIndex];
                // 사용자가 찾아낸 global 통합 엔드포인트 구조 적용
                string url = $"https://aiplatform.googleapis.com/v1/projects/{account.project_id}/locations/global/publishers/google/models/{selectedEndpoint}:generateContent";

                GeminiGenerateRequest requestObject = new GeminiGenerateRequest
                {
                    system_instruction = new SystemInstructionData
                    {
                        parts = new RequestPartData[] { new RequestPartData { text = systemPrompt } }
                    },
                    contents = new RequestContentData[]
                    {
                        new RequestContentData
                        {
                            role = "user", // 400 Bad Request 에러 방지
                            parts = new RequestPartData[] { new RequestPartData { text = finalUserPromptToSend } }
                        }
                    }
                };

                string jsonData = JsonUtility.ToJson(requestObject);

                using (UnityWebRequest webRequest = new UnityWebRequest(url, "POST"))
                {
                    byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonData);
                    webRequest.uploadHandler = new UploadHandlerRaw(bodyRaw);
                    webRequest.downloadHandler = new DownloadHandlerBuffer();
                    webRequest.SetRequestHeader("Authorization", $"Bearer {accessToken}");
                    webRequest.SetRequestHeader("Content-Type", "application/json; charset=utf-8");

                    webRequest.timeout = 120;
                    webRequest.certificateHandler = new BypassCertificate();

                    var operation = webRequest.SendWebRequest();
                    var tcs = new TaskCompletionSource<bool>();
                    operation.completed += (op) => { tcs.SetResult(true); };
                    await tcs.Task;

                    if (webRequest.result == UnityWebRequest.Result.Success)
                    {
                        GeminiResponse parsedData = JsonUtility.FromJson<GeminiResponse>(webRequest.downloadHandler.text);
                        if (parsedData != null && parsedData.candidates != null && parsedData.candidates.Length > 0)
                        {
                            string finalAnswer = parsedData.candidates[0].content.parts[0].text;
                            responseText = finalAnswer;
                            ExecuteGADCommands(finalAnswer);
                        }
                        else responseText = "데이터를 분석할 수 없습니다.\n" + webRequest.downloadHandler.text;
                    }
                    else responseText = "통신 에러: " + webRequest.error + "\n" + webRequest.downloadHandler.text;
                }
            }
            catch (Exception e)
            {
                responseText = "실행 중 치명적 오류 발생: " + e.Message;
            }
            finally
            {
                isGenerating = false;
                Repaint();
            }
        }

        private void ExecuteGADCommands(string aiResponse)
        {
            bool isSceneChanged = false;

            // 1. 오브젝트 생성 처리
            MatchCollection createMatches = Regex.Matches(aiResponse, @"\[GAD_CREATE:(.+?)\]");
            foreach (Match match in createMatches)
            {
                string objName = match.Groups[1].Value.Trim();
                GameObject newObj = new GameObject(objName);
                Undo.RegisterCreatedObjectUndo(newObj, $"Create {objName} via GAD");
                Debug.Log($"[GAD] '{objName}' 오브젝트를 생성했습니다.");
                isSceneChanged = true;
            }

            // 2. 컴포넌트 추가 처리
            MatchCollection addCompMatches = Regex.Matches(aiResponse, @"\[GAD_ADD_COMP:(.+?),(.+?)\]");
            foreach (Match match in addCompMatches)
            {
                string objName = match.Groups[1].Value.Trim();
                string compName = match.Groups[2].Value.Trim();

                GameObject targetObj = GameObject.Find(objName);
                if (targetObj != null)
                {
                    Type compType = GetTypeFromAllAssemblies(compName);
                    if (compType != null && targetObj.GetComponent(compType) == null)
                    {
                        Component newComp = Undo.AddComponent(targetObj, compType);
                        Debug.Log($"[GAD] '{objName}'에 '{compName}' 컴포넌트를 추가했습니다.");
                        isSceneChanged = true;
                    }
                }
            }

            // 3. 리스트 인스펙터 자동 할당 처리
            MatchCollection assignListMatches = Regex.Matches(aiResponse, @"\[GAD_ASSIGN_LIST:(.+?),(.+?),(.+?),(.+?)\]");
            foreach (Match match in assignListMatches)
            {
                string targetObjName = match.Groups[1].Value.Trim();
                string compName = match.Groups[2].Value.Trim();
                string fieldName = match.Groups[3].Value.Trim();
                string objectsToAssignStr = match.Groups[4].Value.Trim();

                GameObject targetObj = GameObject.Find(targetObjName);
                if (targetObj == null) continue;

                Component targetComp = targetObj.GetComponent(compName);
                if (targetComp == null) continue;

                Type type = targetComp.GetType();
                FieldInfo field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                if (field != null && field.FieldType == typeof(List<GameObject>))
                {
                    Undo.RecordObject(targetComp, $"Assign List {fieldName} via GAD");

                    List<GameObject> list = (List<GameObject>)field.GetValue(targetComp);
                    if (list == null) list = new List<GameObject>();

                    // 콤마로 구분된 할당 대상 오브젝트 이름들을 분리하여 리스트에 추가
                    string[] objNamesToAssign = objectsToAssignStr.Split(',');
                    int addedCount = 0;

                    foreach (string objName in objNamesToAssign)
                    {
                        string cleanName = objName.Trim();
                        GameObject go = GameObject.Find(cleanName);
                        if (go != null && !list.Contains(go))
                        {
                            list.Add(go);
                            addedCount++;
                        }
                    }

                    field.SetValue(targetComp, list);
                    EditorUtility.SetDirty(targetComp);
                    Debug.Log($"[GAD] '{targetObjName}'의 '{fieldName}' 리스트에 {addedCount}개의 오브젝트를 성공적으로 할당했습니다.");
                    isSceneChanged = true;
                }
            }

            // 4. 단일 레퍼런스(참조) 할당 처리
            MatchCollection assignRefMatches = Regex.Matches(aiResponse, @"\[GAD_ASSIGN_REF:(.+?),(.+?),(.+?),(.+?)\]");
            foreach (Match match in assignRefMatches)
            {
                string targetObjName = match.Groups[1].Value.Trim();
                string compName = match.Groups[2].Value.Trim();
                string fieldName = match.Groups[3].Value.Trim();
                string objToAssignName = match.Groups[4].Value.Trim();

                GameObject targetObj = GameObject.Find(targetObjName);
                GameObject objToAssign = GameObject.Find(objToAssignName);

                if (targetObj == null || objToAssign == null) continue;

                Component targetComp = targetObj.GetComponent(compName);
                if (targetComp == null) continue;

                Type type = targetComp.GetType();
                FieldInfo field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                if (field != null)
                {
                    Undo.RecordObject(targetComp, $"Assign Ref {fieldName} via GAD");

                    if (field.FieldType == typeof(GameObject))
                    {
                        field.SetValue(targetComp, objToAssign);
                        EditorUtility.SetDirty(targetComp);
                        Debug.Log($"[GAD] '{targetObjName}'의 '{fieldName}'에 '{objToAssignName}' GameObject를 할당했습니다.");
                        isSceneChanged = true;
                    }
                    else if (typeof(Component).IsAssignableFrom(field.FieldType))
                    {
                        Component compToAssign = objToAssign.GetComponent(field.FieldType);
                        if (compToAssign != null)
                        {
                            field.SetValue(targetComp, compToAssign);
                            EditorUtility.SetDirty(targetComp);
                            Debug.Log($"[GAD] '{targetObjName}'의 '{fieldName}'에 '{objToAssignName}'의 Component를 할당했습니다.");
                            isSceneChanged = true;
                        }
                    }
                }
            }

            // 5. 프리팹 인스턴스화 (프로젝트 에셋을 씬으로 호출)
            MatchCollection instantiateMatches = Regex.Matches(aiResponse, @"\[GAD_INSTANTIATE:(.+?),(.+?)\]");
            foreach (Match match in instantiateMatches)
            {
                string prefabName = match.Groups[1].Value.Trim();
                string newObjName = match.Groups[2].Value.Trim();

                string[] guids = AssetDatabase.FindAssets(prefabName + " t:Prefab");
                if (guids.Length > 0)
                {
                    string assetPath = AssetDatabase.GUIDToAssetPath(guids[0]);
                    GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                    if (prefab != null)
                    {
                        GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                        instance.name = newObjName;
                        Undo.RegisterCreatedObjectUndo(instance, $"Instantiate {newObjName} via GAD");
                        Debug.Log($"[GAD] '{prefabName}' 프리팹을 '{newObjName}'(으)로 씬에 생성했습니다.");
                        isSceneChanged = true;
                    }
                }
                else
                {
                    Debug.LogWarning($"[GAD] '{prefabName}' 프리팹을 프로젝트에서 찾을 수 없습니다.");
                }
            }

            // 6. 다중 오브젝트 일괄 삭제 (스마트 부분 검색 및 대소문자 무시 적용)
            MatchCollection deleteMatches = Regex.Matches(aiResponse, @"\[GAD_DELETE:(.+?)\]");
            if (deleteMatches.Count > 0)
            {
                List<GameObject> objectsToDelete = new List<GameObject>();
                List<string> notFoundNames = new List<string>();

                // 씬에 존재하는 모든 오브젝트를 배열로 가져옵니다.
                GameObject[] allObjects = Resources.FindObjectsOfTypeAll<GameObject>();

                foreach (Match match in deleteMatches)
                {
                    string searchKeyword = match.Groups[1].Value.Trim().ToLower(); // 대소문자 무시를 위해 소문자로 변환
                    bool isFound = false;

                    foreach (GameObject go in allObjects)
                    {
                        // 프로젝트 에셋이 아닌 현재 씬에 로드된 오브젝트 중, 이름에 키워드가 포함된 모든 오브젝트 수집
                        if (go.scene.isLoaded && go.name.ToLower().Contains(searchKeyword))
                        {
                            if (!objectsToDelete.Contains(go))
                            {
                                objectsToDelete.Add(go);
                            }
                            isFound = true;
                        }
                    }

                    if (!isFound)
                    {
                        notFoundNames.Add(match.Groups[1].Value.Trim());
                    }
                }

                if (objectsToDelete.Count > 0)
                {
                    GADDeleteConfirmWindow.ShowWindow(objectsToDelete);
                }

                if (notFoundNames.Count > 0)
                {
                    Debug.LogWarning($"[GAD] '{string.Join(", ", notFoundNames)}' 키워드가 포함된 오브젝트를 씬에서 찾을 수 없습니다.");
                }
            }
            // 7. 위치 이동 처리
            MatchCollection setPosMatches = Regex.Matches(aiResponse, @"\[GAD_SET_POSITION:(.+?),(.+?),(.+?),(.+?)\]");
            foreach (Match match in setPosMatches)
            {
                string objName = match.Groups[1].Value.Trim();
                if (float.TryParse(match.Groups[2].Value, out float x) &&
                    float.TryParse(match.Groups[3].Value, out float y) &&
                    float.TryParse(match.Groups[4].Value, out float z))
                {
                    GameObject targetObj = GameObject.Find(objName);
                    if (targetObj != null)
                    {
                        Undo.RecordObject(targetObj.transform, $"Set Position {objName} via GAD");
                        targetObj.transform.position = new Vector3(x, y, z);
                        Debug.Log($"[GAD] '{objName}'의 위치를 ({x}, {y}, {z})로 이동했습니다.");
                        isSceneChanged = true;
                    }
                }
            }

            // 8. 일반 변수 수치 변경 처리
            MatchCollection setValueMatches = Regex.Matches(aiResponse, @"\[GAD_SET_VALUE:(.+?),(.+?),(.+?),(.+?)\]");
            foreach (Match match in setValueMatches)
            {
                string objName = match.Groups[1].Value.Trim();
                string compName = match.Groups[2].Value.Trim();
                string fieldName = match.Groups[3].Value.Trim();
                string valueStr = match.Groups[4].Value.Trim();

                GameObject targetObj = GameObject.Find(objName);
                if (targetObj == null) continue;

                Component targetComp = targetObj.GetComponent(compName);
                if (targetComp == null) continue;

                Type type = targetComp.GetType();
                FieldInfo field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                if (field != null)
                {
                    Undo.RecordObject(targetComp, $"Set Value {fieldName} via GAD");
                    try
                    {
                        object parsedValue = null;
                        Type fieldType = field.FieldType;

                        if (fieldType == typeof(int)) parsedValue = int.Parse(valueStr);
                        else if (fieldType == typeof(float)) parsedValue = float.Parse(valueStr);
                        else if (fieldType == typeof(bool)) parsedValue = bool.Parse(valueStr);
                        else if (fieldType == typeof(string)) parsedValue = valueStr;
                        else if (fieldType.IsEnum) parsedValue = Enum.Parse(fieldType, valueStr.Replace(" ", ""), true);

                        if (parsedValue != null)
                        {
                            field.SetValue(targetComp, parsedValue);
                            EditorUtility.SetDirty(targetComp);
                            Debug.Log($"[GAD] '{objName}'의 '{fieldName}' 값을 '{valueStr}'(으)로 변경했습니다.");
                            isSceneChanged = true;
                        }
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[GAD] '{fieldName}'에 '{valueStr}' 값을 할당하는 중 오류 발생: {e.Message}");
                    }
                }
            }

            // 9. 리스트 내부 변수 수치 변경 처리
            MatchCollection setListValueMatches = Regex.Matches(aiResponse, @"\[GAD_SET_LIST_VALUE:(.+?),(.+?),(.+?),(.+?),(.+?),(.+?)\]");
            foreach (Match match in setListValueMatches)
            {
                string objName = match.Groups[1].Value.Trim();
                string compName = match.Groups[2].Value.Trim();
                string listName = match.Groups[3].Value.Trim();
                if (!int.TryParse(match.Groups[4].Value.Trim(), out int index)) continue;
                string innerFieldName = match.Groups[5].Value.Trim();
                string valueStr = match.Groups[6].Value.Trim();

                GameObject targetObj = GameObject.Find(objName);
                if (targetObj == null) continue;

                Component targetComp = targetObj.GetComponent(compName);
                if (targetComp == null) continue;

                Type type = targetComp.GetType();
                System.Reflection.FieldInfo listField = type.GetField(listName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                if (listField != null && typeof(System.Collections.IList).IsAssignableFrom(listField.FieldType))
                {
                    System.Collections.IList list = listField.GetValue(targetComp) as System.Collections.IList;
                    if (list != null && index >= 0 && index < list.Count)
                    {
                        object item = list[index];
                        if (item != null)
                        {
                            Type itemType = item.GetType();
                            System.Reflection.FieldInfo innerField = itemType.GetField(innerFieldName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            if (innerField != null)
                            {
                                Undo.RecordObject(targetComp, $"Set List Value via GAD");
                                try
                                {
                                    object parsedValue = null;
                                    Type fieldType = innerField.FieldType;

                                    if (fieldType == typeof(int)) parsedValue = int.Parse(valueStr);
                                    else if (fieldType == typeof(float)) parsedValue = float.Parse(valueStr);
                                    else if (fieldType == typeof(bool)) parsedValue = bool.Parse(valueStr);
                                    else if (fieldType == typeof(string)) parsedValue = valueStr;
                                    else if (fieldType.IsEnum) parsedValue = Enum.Parse(fieldType, valueStr.Replace(" ", ""), true);

                                    if (parsedValue != null)
                                    {
                                        innerField.SetValue(item, parsedValue);
                                        EditorUtility.SetDirty(targetComp);
                                        Debug.Log($"[GAD] '{objName}'의 '{listName}[{index}].{innerFieldName}' 값을 '{valueStr}'(으)로 변경했습니다.");
                                        isSceneChanged = true;
                                    }
                                }
                                catch (Exception e)
                                {
                                    Debug.LogWarning($"[GAD] 오류: {e.Message}");
                                }
                            }
                        }
                    }
                }
            }

            // 10. 부모-자식 관계 설정 (하이라키 정리)
            MatchCollection setParentMatches = Regex.Matches(aiResponse, @"\[GAD_SET_PARENT:(.+?),(.+?)\]");
            foreach (Match match in setParentMatches)
            {
                string childName = match.Groups[1].Value.Trim();
                string parentName = match.Groups[2].Value.Trim();

                GameObject childObj = GameObject.Find(childName);

                // parentName이 None이거나 Null이면 최상단(Root)으로 빼냄
                GameObject parentObj = (parentName.ToLower() == "none" || parentName.ToLower() == "null") ? null : GameObject.Find(parentName);

                if (childObj != null && (parentObj != null || parentName.ToLower() == "none" || parentName.ToLower() == "null"))
                {
                    Undo.SetTransformParent(childObj.transform, parentObj != null ? parentObj.transform : null, $"Set Parent {childName} via GAD");
                    Debug.Log($"[GAD] '{childName}' 오브젝트를 '{parentName}'의 자식으로 이동했습니다.");
                    isSceneChanged = true;
                }
            }

            // 11. 태그 변경 처리
            MatchCollection setTagMatches = Regex.Matches(aiResponse, @"\[GAD_SET_TAG:(.+?),(.+?)\]");
            foreach (Match match in setTagMatches)
            {
                string objName = match.Groups[1].Value.Trim();
                string tagName = match.Groups[2].Value.Trim();

                GameObject targetObj = GameObject.Find(objName);
                if (targetObj != null)
                {
                    Undo.RecordObject(targetObj, $"Set Tag {objName} via GAD");
                    try
                    {
                        targetObj.tag = tagName;
                        Debug.Log($"[GAD] '{objName}'의 태그를 '{tagName}'(으)로 변경했습니다.");
                        isSceneChanged = true;
                    }
                    catch
                    {
                        Debug.LogWarning($"[GAD] '{tagName}' 태그가 프로젝트에 정의되어 있지 않아 적용할 수 없습니다.");
                    }
                }
            }

            // 12. 레이어 변경 처리
            MatchCollection setLayerMatches = Regex.Matches(aiResponse, @"\[GAD_SET_LAYER:(.+?),(.+?)\]");
            foreach (Match match in setLayerMatches)
            {
                string objName = match.Groups[1].Value.Trim();
                string layerName = match.Groups[2].Value.Trim();

                GameObject targetObj = GameObject.Find(objName);
                if (targetObj != null)
                {
                    int layerIndex = LayerMask.NameToLayer(layerName);
                    if (layerIndex != -1)
                    {
                        Undo.RecordObject(targetObj, $"Set Layer {objName} via GAD");
                        targetObj.layer = layerIndex;
                        Debug.Log($"[GAD] '{objName}'의 레이어를 '{layerName}'(으)로 변경했습니다.");
                        isSceneChanged = true;
                    }
                    else
                    {
                        Debug.LogWarning($"[GAD] '{layerName}' 레이어가 프로젝트에 정의되어 있지 않아 적용할 수 없습니다.");
                    }
                }
            }

            // 13. 회전(Rotation) 변경 처리
            MatchCollection setRotMatches = Regex.Matches(aiResponse, @"\[GAD_SET_ROTATION:(.+?),(.+?),(.+?),(.+?)\]");
            foreach (Match match in setRotMatches)
            {
                string objName = match.Groups[1].Value.Trim();
                if (float.TryParse(match.Groups[2].Value, out float x) &&
                    float.TryParse(match.Groups[3].Value, out float y) &&
                    float.TryParse(match.Groups[4].Value, out float z))
                {
                    GameObject targetObj = GameObject.Find(objName);
                    if (targetObj != null)
                    {
                        Undo.RecordObject(targetObj.transform, $"Set Rotation {objName} via GAD");
                        // 인스펙터의 수치와 동일하게 작동하도록 localEulerAngles 사용
                        targetObj.transform.localEulerAngles = new Vector3(x, y, z);
                        Debug.Log($"[GAD] '{objName}'의 회전을 ({x}, {y}, {z})(으)로 변경했습니다.");
                        isSceneChanged = true;
                    }
                }
            }

            // 14. 크기(Scale) 변경 처리
            MatchCollection setScaleMatches = Regex.Matches(aiResponse, @"\[GAD_SET_SCALE:(.+?),(.+?),(.+?),(.+?)\]");
            foreach (Match match in setScaleMatches)
            {
                string objName = match.Groups[1].Value.Trim();
                if (float.TryParse(match.Groups[2].Value, out float x) &&
                    float.TryParse(match.Groups[3].Value, out float y) &&
                    float.TryParse(match.Groups[4].Value, out float z))
                {
                    GameObject targetObj = GameObject.Find(objName);
                    if (targetObj != null)
                    {
                        Undo.RecordObject(targetObj.transform, $"Set Scale {objName} via GAD");
                        targetObj.transform.localScale = new Vector3(x, y, z);
                        Debug.Log($"[GAD] '{objName}'의 크기를 ({x}, {y}, {z})(으)로 변경했습니다.");
                        isSceneChanged = true;
                    }
                }
            }

            // 15. 오브젝트 활성화/비활성화 처리
            MatchCollection setActiveMatches = Regex.Matches(aiResponse, @"\[GAD_SET_ACTIVE:(.+?),(.+?)\]");
            foreach (Match match in setActiveMatches)
            {
                string objName = match.Groups[1].Value.Trim();
                if (bool.TryParse(match.Groups[2].Value.Trim(), out bool isActive))
                {
                    // 꺼진 오브젝트도 찾을 수 있도록 씬의 모든 오브젝트를 검색합니다.
                    GameObject targetObj = null;
                    GameObject[] allObjects = Resources.FindObjectsOfTypeAll<GameObject>();
                    foreach (GameObject go in allObjects)
                    {
                        if (go.scene.isLoaded && go.name == objName)
                        {
                            targetObj = go;
                            break;
                        }
                    }

                    if (targetObj != null && targetObj.activeSelf != isActive)
                    {
                        Undo.RecordObject(targetObj, $"Set Active {objName} via GAD");
                        targetObj.SetActive(isActive);
                        Debug.Log($"[GAD] '{objName}' 오브젝트를 {(isActive ? "활성화" : "비활성화")} 했습니다.");
                        isSceneChanged = true;
                    }
                }
            }

            // 16. 컴포넌트 활성화/비활성화 처리
            MatchCollection setEnableMatches = Regex.Matches(aiResponse, @"\[GAD_SET_ENABLE:(.+?),(.+?),(.+?)\]");
            foreach (Match match in setEnableMatches)
            {
                string objName = match.Groups[1].Value.Trim();
                string compName = match.Groups[2].Value.Trim();
                if (bool.TryParse(match.Groups[3].Value.Trim(), out bool isEnabled))
                {
                    GameObject targetObj = GameObject.Find(objName);
                    if (targetObj != null)
                    {
                        Component targetComp = targetObj.GetComponent(compName);
                        if (targetComp != null && targetComp is Behaviour behaviour)
                        {
                            Undo.RecordObject(behaviour, $"Set Enable {compName} via GAD");
                            behaviour.enabled = isEnabled;
                            Debug.Log($"[GAD] '{objName}'의 '{compName}' 컴포넌트를 {(isEnabled ? "활성화" : "비활성화")} 했습니다.");
                            isSceneChanged = true;
                        }
                    }
                }
            }

            // 17. 머티리얼(Material) 변경 처리
            MatchCollection setMaterialMatches = Regex.Matches(aiResponse, @"\[GAD_SET_MATERIAL:(.+?),(.+?)\]");
            foreach (Match match in setMaterialMatches)
            {
                string objName = match.Groups[1].Value.Trim();
                string matName = match.Groups[2].Value.Trim();

                GameObject targetObj = GameObject.Find(objName);
                if (targetObj != null)
                {
                    Renderer renderer = targetObj.GetComponent<Renderer>();
                    if (renderer != null)
                    {
                        string[] guids = AssetDatabase.FindAssets(matName + " t:Material");
                        if (guids.Length > 0)
                        {
                            string assetPath = AssetDatabase.GUIDToAssetPath(guids[0]);
                            Material mat = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
                            if (mat != null)
                            {
                                Undo.RecordObject(renderer, $"Set Material {objName} via GAD");
                                // 에디터 환경이므로 메모리 누수를 막기 위해 sharedMaterial을 사용합니다.
                                renderer.sharedMaterial = mat;
                                Debug.Log($"[GAD] '{objName}'의 머티리얼을 '{matName}'(으)로 변경했습니다.");
                                isSceneChanged = true;
                            }
                        }
                        else
                        {
                            Debug.LogWarning($"[GAD] '{matName}' 머티리얼을 프로젝트에서 찾을 수 없습니다.");
                        }
                    }
                    else
                    {
                        Debug.LogWarning($"[GAD] '{objName}'에 Renderer 컴포넌트가 없어 머티리얼을 적용할 수 없습니다.");
                    }
                }
            }

            // 19. 오브젝트 복제 처리 (이름 변경보다 무조건 먼저 실행되어야 합니다)
            MatchCollection duplicateMatches = Regex.Matches(aiResponse, @"\[GAD_DUPLICATE:(.+?)\]");
            foreach (Match match in duplicateMatches)
            {
                string objName = match.Groups[1].Value.Trim();
                GameObject targetObj = GameObject.Find(objName);
                if (targetObj != null)
                {
                    GameObject duplicatedObj = Instantiate(targetObj, targetObj.transform.parent);
                    duplicatedObj.name = targetObj.name + "_Copy";
                    Undo.RegisterCreatedObjectUndo(duplicatedObj, $"Duplicate {objName} via GAD");
                    Debug.Log($"[GAD] '{objName}' 오브젝트를 복제하여 '{duplicatedObj.name}'을(를) 생성했습니다.");
                    isSceneChanged = true;
                }
            }

            // 18. 이름 변경 처리 (복제가 끝난 뒤에 이름을 찾아서 변경합니다)
            MatchCollection renameMatches = Regex.Matches(aiResponse, @"\[GAD_RENAME:(.+?),(.+?)\]");
            foreach (Match match in renameMatches)
            {
                string oldName = match.Groups[1].Value.Trim();
                string newName = match.Groups[2].Value.Trim();

                GameObject targetObj = GameObject.Find(oldName);
                if (targetObj != null)
                {
                    Undo.RecordObject(targetObj, $"Rename {oldName} to {newName} via GAD");
                    targetObj.name = newName;
                    Debug.Log($"[GAD] '{oldName}'의 이름을 '{newName}'(으)로 변경했습니다.");
                    isSceneChanged = true;
                }
            }

            if (isSceneChanged)
            {
                EditorApplication.RepaintHierarchyWindow();
            }
        }

        private Type GetTypeFromAllAssemblies(string typeName)
        {
            Type type = Type.GetType(typeName);
            if (type != null) return type;

            type = Type.GetType("UnityEngine." + typeName + ", UnityEngine");
            if (type != null) return type;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = assembly.GetType(typeName);
                if (type != null) return type;
            }
            return null;
        }

        private void SaveSettingsToFile()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine(jsonFilePath);
                sb.AppendLine(gcpRegion);
                sb.Append(systemPrompt);
                File.WriteAllText(settingsFilePath, sb.ToString(), Encoding.UTF8);
                Debug.Log($"[GAD] 설정이 성공적으로 저장되었습니다.");
            }
            catch (Exception e) { Debug.LogError($"[GAD] 설정 저장 실패: {e.Message}"); }
        }

        private void LoadSettingsFromFile()
        {
            try
            {
                if (File.Exists(settingsFilePath))
                {
                    string[] lines = File.ReadAllLines(settingsFilePath);
                    if (lines.Length > 0) jsonFilePath = lines[0].Trim();
                    if (lines.Length > 1) gcpRegion = lines[1].Trim();
                    if (lines.Length > 2) systemPrompt = string.Join("\n", lines, 2, lines.Length - 2).Trim();
                }
                else
                {
                    systemPrompt = "[GAD 시스템 규칙]\n" +
                                   "당신은 유니티 에디터를 제어하는 AI입니다. 사용자의 요구에 맞춰 아래의 태그를 출력하여 물리적 작업을 수행하세요.\n\n" +
                                   "1. 오브젝트 생성: [GAD_CREATE:오브젝트이름]\n" +
                                   "2. 컴포넌트 추가: [GAD_ADD_COMP:오브젝트이름,컴포넌트이름]\n" +
                                   "3. 리스트 변수 할당: [GAD_ASSIGN_LIST:타겟명,컴포넌트명,리스트변수명,할당할오브젝트1,오브젝트2...]\n" +
                                   "4. 단일 변수 할당: [GAD_ASSIGN_REF:타겟명,컴포넌트명,변수명,할당할오브젝트명]\n" +
                                   "5. 프리팹 생성: [GAD_INSTANTIATE:프리팹이름,생성할이름]\n" +
                                   "6. 오브젝트 삭제: [GAD_DELETE:삭제할오브젝트이름]\n" +
                                   "7. 위치 이동: [GAD_SET_POSITION:오브젝트명,X,Y,Z]\n" +
                                   "8. 수치 변경: [GAD_SET_VALUE:오브젝트명,컴포넌트명,변수명,값]\n" +
                                   "9. 리스트 내부 수치 변경: [GAD_SET_LIST_VALUE:오브젝트명,컴포넌트명,리스트변수명,인덱스번호,내부변수명,값]\n" +
                                   "10. 부모 설정: [GAD_SET_PARENT:자식오브젝트명,부모오브젝트명]\n" +
                                   "11. 태그 설정: [GAD_SET_TAG:오브젝트명,태그명]\n" +
                                   "12. 레이어 설정: [GAD_SET_LAYER:오브젝트명,레이어명]\n" +
                                   "13. 회전 설정: [GAD_SET_ROTATION:오브젝트명,X,Y,Z]\n" +
                                   "14. 크기 설정: [GAD_SET_SCALE:오브젝트명,X,Y,Z]\n" +
                                   "15. 오브젝트 활성화: [GAD_SET_ACTIVE:오브젝트명,True/False]\n" +
                                   "16. 컴포넌트 활성화: [GAD_SET_ENABLE:오브젝트명,컴포넌트명,True/False]\n" +
                                   "17. 머티리얼 설정: [GAD_SET_MATERIAL:오브젝트명,머티리얼명]\n" +
                                   "18. 이름 변경: [GAD_RENAME:현재이름,새이름]\n" +
                                   "19. 오브젝트 복제: [GAD_DUPLICATE:복제할오브젝트명] (참고: 복제된 오브젝트의 이름은 '원본이름_Copy'가 됨)\n\n" +
                                   "예시: [GAD_RENAME:Staff 1,Main Staff] [GAD_DUPLICATE:Director]";
                }
            }
            catch (Exception e) { Debug.LogError($"[GAD] 설정 불러오기 실패: {e.Message}"); }
        }
    }

    [System.Serializable]
    public class GeminiResponse { public Candidate[] candidates; }
    [System.Serializable]
    public class Candidate { public Content content; }
    [System.Serializable]
    public class Content { public Part[] parts; }
    [System.Serializable]
    public class Part { public string text; }

    public class GADDeleteConfirmWindow : EditorWindow
    {
        private class DeleteItem
        {
            public GameObject obj;
            public bool isChecked;
        }

        private List<DeleteItem> itemsToConfirm = new List<DeleteItem>();
        private Vector2 scrollPos;

        public static void ShowWindow(List<GameObject> objectsToDelete)
        {
            GADDeleteConfirmWindow window = GetWindow<GADDeleteConfirmWindow>("GAD 삭제 검토");
            window.Initialize(objectsToDelete);
            window.Show();
        }

        private void Initialize(List<GameObject> objectsToDelete)
        {
            itemsToConfirm.Clear();
            foreach (var obj in objectsToDelete)
            {
                if (obj != null)
                {
                    // 창이 열릴 때 기본적으로 모두 체크된 상태로 만듭니다.
                    itemsToConfirm.Add(new DeleteItem { obj = obj, isChecked = true });
                }
            }
        }

        private void OnGUI()
        {
            GUILayout.Space(10);
            GUILayout.Label($"총 {itemsToConfirm.Count}개의 오브젝트가 삭제 대기 중입니다.");
            GUILayout.Label("체크를 해제하면 삭제 대상에서 안전하게 제외됩니다.");
            GUILayout.Space(10);

            // 160개가 넘어도 안전하게 확인할 수 있는 스크롤 영역 생성
            scrollPos = EditorGUILayout.BeginScrollView(scrollPos);
            for (int i = 0; i < itemsToConfirm.Count; i++)
            {
                if (itemsToConfirm[i].obj != null)
                {
                    itemsToConfirm[i].isChecked = EditorGUILayout.ToggleLeft(itemsToConfirm[i].obj.name, itemsToConfirm[i].isChecked);
                }
            }
            EditorGUILayout.EndScrollView();

            GUILayout.Space(10);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("전체 선택", GUILayout.Height(30))) SetAll(true);
            if (GUILayout.Button("전체 해제", GUILayout.Height(30))) SetAll(false);
            EditorGUILayout.EndHorizontal();

            GUILayout.Space(10);

            GUI.backgroundColor = Color.red;
            if (GUILayout.Button("체크된 항목 일괄 삭제 실행", GUILayout.Height(40)))
            {
                ExecuteDeletion();
            }
            GUI.backgroundColor = Color.white;

            GUILayout.Space(10);
        }

        private void SetAll(bool state)
        {
            foreach (var item in itemsToConfirm)
            {
                item.isChecked = state;
            }
        }

        private void ExecuteDeletion()
        {
            int deletedCount = 0;
            foreach (var item in itemsToConfirm)
            {
                if (item.isChecked && item.obj != null)
                {
                    string objName = item.obj.name;
                    Undo.DestroyObjectImmediate(item.obj);
                    Debug.Log($"[GAD] '{objName}' 오브젝트를 삭제했습니다.");
                    deletedCount++;
                }
            }

            if (deletedCount > 0)
            {
                Debug.Log($"[GAD] 사용자의 최종 승인 하에 총 {deletedCount}개의 오브젝트가 일괄 삭제되었습니다.");
                EditorApplication.RepaintHierarchyWindow();
            }

            // 삭제 작업이 끝나면 창을 스스로 닫습니다.
            this.Close();
        }
    }
}