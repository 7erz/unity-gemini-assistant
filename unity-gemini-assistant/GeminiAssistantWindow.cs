using UnityEngine;
using UnityEditor;
using UnityEngine.Networking;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.IO;
using System;
using System.Security.Cryptography;
using UnityEditor.Compilation;

// [개선 포인트] SSL 인증서 무시 (유니티 고질적 에러 방지)
public class BypassCertificate : CertificateHandler
{
    protected override bool ValidateCertificate(byte[] certificateData) => true;
}

// =================================================================
// Vertex AI 통신 및 OAuth 2.0 인증용 데이터 클래스
// =================================================================
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
public class GeminiGenerateRequest
{
    public SystemInstructionData system_instruction;
    public RequestContentData[] contents;
}

[Serializable]
public class SystemInstructionData { public RequestPartData[] parts; }

[Serializable]
public class RequestContentData
{
    public string role;
    public RequestPartData[] parts;
}

[Serializable]
public class RequestPartData { public string text; }

// =================================================================

public class GeminiAssistantWindow : EditorWindow
{
    private struct PendingDiffData
    {
        public string assetPath;
        public string originalContent;
        public string newContent;
    }
    private string jsonFilePath = "";
    private string gcpRegion = "us-central1"; // 기본 리전
    private string systemPrompt = "";
    private string userPrompt = "NGO를 이용해서 NewInputSystem의 이동과 점프를 구현하고 싶어.";
    private string responseText = "";
    private string lastAIResponse = "";
    private Vector2 scrollPos;
    private Vector2 systemPromptScrollPos;
    private Vector2 userPromptScrollPos;

    private int selectedModelIndex = 0;
    // 에디터 창에 보여질 깔끔한 이름들
    private readonly string[] modelDisplayNames = new string[] { "Gemini 2.5 Flash", "Gemini 3.0 Flash", "Gemini 3.1 Pro" };

    // 구글 서버가 인식하는 정확한 시스템 엔드포인트 이름
    private readonly string[] modelEndpoints = new string[] { "gemini-2.5-flash", "gemini-3-flash-preview", "gemini-3.1-pro-preview" };

    private int selectedPresetIndex = 0;
    private readonly string[] presetTitles = new string[]
    {
        "직접 입력",
        "한국어 주석 상세 추가",
        "코드 최적화 및 리팩토링",
        "싱글톤 패턴으로 변환",
        "Debug.Log 모두 제거"
    };

    private readonly string[] presetContents = new string[]
    {
        "",
        "첨부된 코드의 핵심 로직마다 초보자도 이해하기 쉽도록 상세한 한국어 주석을 추가해 줘. 기존 로직은 변경하지 마.",
        "첨부된 코드의 불필요한 메모리 할당(GC)을 줄이고, 성능이 최적화되도록 리팩토링해 줘. 수정된 부분에 이유를 주석으로 달아줘.",
        "이 클래스를 유니티 C# 싱글톤 패턴으로 변환해 줘. 스레드 세이프는 고려하지 않아도 되며, Awake에서 초기화되도록 해줘.",
        "첨부된 코드 안에 있는 모든 Debug.Log, Debug.LogWarning, Debug.LogError 문을 찾아서 안전하게 삭제해 줘."
    };

    private class FileSlot
    {
        public MonoScript script;
        public bool isModifiable = false;
    }

    private List<FileSlot> targetScripts = new List<FileSlot>() { new FileSlot() };

    private bool autoSaveLog = true;
    private string logDirectoryPath;
    private string settingsFilePath;
    private bool isGenerating = false;

    // --- [자동 디버깅용 추가 변수] ---
    private int currentRetryCount = 0;
    private const int MAX_RETRIES = 2; // 최대 스스로 고치는 횟수
    private bool isWaitingForCompilation = false;
    private List<string> currentCompileErrors = new List<string>();

    

    [MenuItem("Tools/Gemini Assistant")]
    public static void ShowWindow()
    {
        GetWindow<GeminiAssistantWindow>("Gemini Assistant");
    }

    private void OnEnable()
    {
        logDirectoryPath = Path.Combine(Application.dataPath, "GeminiLogs");
        settingsFilePath = Path.Combine(Application.dataPath, "../Gemini_Settings.txt");
        LoadSettingsFromFile();

        // 컴파일 완료 이벤트 구독
        CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
        CompilationPipeline.compilationFinished += OnCompilationFinished;
    }

    private void OnDisable()
    {
        // 메모리 누수 방지를 위해 이벤트 구독 해제
        CompilationPipeline.assemblyCompilationFinished -= OnAssemblyCompilationFinished;
        CompilationPipeline.compilationFinished -= OnCompilationFinished;
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

        GUILayout.Space(5);

        EditorGUILayout.BeginHorizontal();
        autoSaveLog = EditorGUILayout.Toggle("로그 자동 저장", autoSaveLog);
        if (GUILayout.Button("로그 폴더 열기", GUILayout.Width(150))) OpenLogFolder();
        EditorGUILayout.EndHorizontal();

        GUILayout.Label("System Prompt (역할 및 규칙)", EditorStyles.boldLabel);
        systemPromptScrollPos = EditorGUILayout.BeginScrollView(systemPromptScrollPos, GUILayout.Height(85));
        systemPrompt = EditorGUILayout.TextArea(systemPrompt, GUILayout.ExpandHeight(true));
        EditorGUILayout.EndScrollView();

        GUILayout.Space(10);
        GUILayout.Label("Drag And Drop (코드 파일 첨부)", EditorStyles.boldLabel);

        for (int i = 0; i < targetScripts.Count; i++)
        {
            EditorGUILayout.BeginHorizontal();
            targetScripts[i].script = (MonoScript)EditorGUILayout.ObjectField($"File {i + 1}", targetScripts[i].script, typeof(MonoScript), false);
            targetScripts[i].isModifiable = EditorGUILayout.ToggleLeft("코드 수정 가능", targetScripts[i].isModifiable, GUILayout.Width(110));

            if (GUILayout.Button("X", GUILayout.Width(30)))
            {
                targetScripts.RemoveAt(i);
                i--;
            }
            EditorGUILayout.EndHorizontal();
        }

        if (GUILayout.Button("+ 파일 슬롯 추가")) targetScripts.Add(new FileSlot());

        GUILayout.Space(10);
        GUILayout.Label("Prompt Preset (명령어 프리셋)", EditorStyles.boldLabel);
        EditorGUI.BeginChangeCheck();
        selectedPresetIndex = EditorGUILayout.Popup("자주 쓰는 명령", selectedPresetIndex, presetTitles);
        if (EditorGUI.EndChangeCheck())
        {
            if (selectedPresetIndex != 0)
            {
                userPrompt = presetContents[selectedPresetIndex];
            }
        }

        GUILayout.Label("User Prompt (사용자 질문)", EditorStyles.boldLabel);
        userPromptScrollPos = EditorGUILayout.BeginScrollView(userPromptScrollPos, GUILayout.Height(105));
        userPrompt = EditorGUILayout.TextArea(userPrompt, GUILayout.ExpandHeight(true));
        EditorGUILayout.EndScrollView();

        GUILayout.Space(10);
        GUILayout.Label("AI Model (모델 선택)", EditorStyles.boldLabel);
        selectedModelIndex = EditorGUILayout.Popup(selectedModelIndex, modelDisplayNames);

        GUILayout.Space(10);

        bool isAnyModifiable = false;
        foreach (var slot in targetScripts)
        {
            if (slot.isModifiable && slot.script != null)
            {
                isAnyModifiable = true;
                break;
            }
        }

        EditorGUI.BeginDisabledGroup(isGenerating || isWaitingForCompilation);

        if (isAnyModifiable)
        {
            GUI.backgroundColor = Color.red;
            string btnText = isGenerating ? "통신 중... 잠시 대기 ⚠️" : "생성 및 자동 덮어쓰기 실행 ⚠️";
            if (GUILayout.Button(btnText, GUILayout.Height(40))) CallGeminiAPI();
            GUI.backgroundColor = Color.white;
        }
        else
        {
            string btnText = isGenerating ? "통신 중... 잠시 대기" : "생성하기 (Generate)";
            if (GUILayout.Button(btnText, GUILayout.Height(40))) CallGeminiAPI();
        }

        EditorGUI.EndDisabledGroup();

        GUILayout.Space(10);
        GUILayout.Label("Response (결과)", EditorStyles.boldLabel);
        scrollPos = EditorGUILayout.BeginScrollView(scrollPos);
        responseText = EditorGUILayout.TextArea(responseText, GUILayout.ExpandHeight(true));
        EditorGUILayout.EndScrollView();
    }

    private static string Base64UrlEncode(byte[] input)
    {
        string output = Convert.ToBase64String(input);
        output = output.Split('=')[0];
        output = output.Replace('+', '-');
        output = output.Replace('/', '_');
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
        privateKeyStr = privateKeyStr.Replace("-----BEGIN PRIVATE KEY-----", "");
        privateKeyStr = privateKeyStr.Replace("-----END PRIVATE KEY-----", "");
        privateKeyStr = privateKeyStr.Replace("\n", "").Replace("\r", "");
        byte[] privateKeyBytes = Convert.FromBase64String(privateKeyStr);

        using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider())
        {
            // 수동 파싱 함수 호출
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
                else
                {
                    throw new Exception("OAuth 토큰 발급 실패: " + webRequest.downloadHandler.text);
                }
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
            parameters.Modulus = ReadDerInteger(binr);
            parameters.Exponent = ReadDerInteger(binr);
            parameters.D = ReadDerInteger(binr);
            parameters.P = ReadDerInteger(binr);
            parameters.Q = ReadDerInteger(binr);
            parameters.DP = ReadDerInteger(binr);
            parameters.DQ = ReadDerInteger(binr);
            parameters.InverseQ = ReadDerInteger(binr);

            return parameters;
        }
    }

    private static int DecodeDerLength(BinaryReader reader)
    {
        byte b = reader.ReadByte();
        if ((b & 0x80) == 0) return b;
        int count = b & 0x7F;
        int len = 0;
        for (int i = 0; i < count; i++) len = (len << 8) | reader.ReadByte();
        return len;
    }

    private static byte[] ReadDerInteger(BinaryReader reader)
    {
        byte tag = reader.ReadByte();
        if (tag != 0x02) throw new Exception("Expected Integer");
        int length = DecodeDerLength(reader);
        byte[] data = reader.ReadBytes(length);
        if (data.Length > 1 && data[0] == 0x00)
        {
            byte[] trimmed = new byte[data.Length - 1];
            Buffer.BlockCopy(data, 1, trimmed, 0, trimmed.Length);
            return trimmed;
        }
        return data;
    }

    // 매개변수로 isAutoDebug 추가
    private async void CallGeminiAPI(bool isAutoDebug = false)
    {
        if (isGenerating) return;

        if (string.IsNullOrEmpty(jsonFilePath) || !File.Exists(jsonFilePath))
        {
            responseText = "GCP 서비스 계정 JSON 파일 경로가 올바르지 않습니다.";
            return;
        }

        // 사용자가 직접 누른 거라면 재시도 카운트 초기화
        if (!isAutoDebug) currentRetryCount = 0;

        isGenerating = true;
        responseText = isAutoDebug ?
            $"[자동 디버깅 중... {currentRetryCount}/{MAX_RETRIES}]\n에러를 분석하고 코드를 재작성하고 있습니다..." :
            $"{modelDisplayNames[selectedModelIndex]} (Vertex AI) 모델로 답변을 생성하는 중입니다...\n(OAuth 2.0 토큰 발급 및 통신 중...)";
        Repaint();

        try
        {
            string jsonText = File.ReadAllText(jsonFilePath);
            GCPServiceAccount account = JsonUtility.FromJson<GCPServiceAccount>(jsonText);
            string accessToken = await GetOAuthTokenAsync(account);

            StringBuilder fileListBuilder = new StringBuilder();
            StringBuilder codeContentBuilder = new StringBuilder();
            int attachedCount = 0;

            foreach (var slot in targetScripts)
            {
                if (slot.script != null)
                {
                    fileListBuilder.AppendLine($"- {slot.script.name}.cs");
                    codeContentBuilder.AppendLine($"\n// --- {slot.script.name}.cs 시작 ---");
                    codeContentBuilder.AppendLine(slot.script.text);
                    codeContentBuilder.AppendLine($"// --- {slot.script.name}.cs 끝 ---\n");
                    attachedCount++;
                }
            }

            string finalUserPromptToSend = userPrompt;

            if (isAutoDebug)
            {
                finalUserPromptToSend = $"앞서 네가 덮어쓴 코드에서 다음과 같은 유니티 컴파일 에러가 발생했어.\n\n[에러 로그]\n{string.Join("\n", currentCompileErrors)}\n\n이 에러가 발생한 원인을 정확히 분석하고, 에러가 해결된 전체 코드를 다시 작성해서 보내줘.\n\n" +
        "[자동 디버깅 핵심 원칙]\n" +
        "1. 오직 발생한 컴파일 에러를 해결하는 데 필요한 최소한의 코드만 수정하세요.\n" +
        "2. 에러 수정과 무관한 원본 로직, 클래스 상속(예: MonoBehaviour 유지), 메서드 구조는 절대 임의로 변경하거나 네트워크(NGO) 규격으로 확장하지 마세요.\n\n" +
        "3. [중요] 만약 에러의 원인이 코드 오류가 아니라, 유니티 에디터에서 인간이 직접 만들어야 하는 에셋이나 설정(예: Input Action 에셋, Tag, Layer 등)이 누락되어 발생한 것이라면, 절대로 코드를 지우거나 주석 처리하여 기능을 훼손하지 마세요! 원본 코드를 그대로 살려두고, 코드 맨 아래에 주석으로 질문자가 유니티 에디터에서 직접 수행해야 할 조치 사항을 단계별로 안내해 주세요.\n\n" +
        "[매우 중요한 시스템 지시사항]\n당신은 에러 수정이 필요한 모든 파일의 코드를 각각 다시 작성해야 합니다.\n여러 파일을 수정할 경우, 반드시 각 파일마다 별도의 태그를 사용하여 출력해 주세요. (예시:\n[FILE_START: 파일A.cs]\nusing UnityEngine;\n// 파일A 코드...\n[FILE_END: 파일A.cs]\n\n[FILE_START: 파일B.cs]\nusing UnityEngine;\n// 파일B 코드...\n[FILE_END: 파일B.cs])\n*주의: 마크다운 코드 파싱을 위해, 저 태그 문자열이 반드시 코드의 시작과 끝에 있어야 합니다. 태그 안쪽에는 마크다운 제목(# 파일명)이나 기타 설명 텍스트를 절대 넣지 말고, 오직 순수한 C# 컴파일이 가능한 코드만 작성하세요.";
            }
            else
            {
                if (attachedCount > 0)
                {
                    finalUserPromptToSend = $"[첨부된 파일 목록]\n{fileListBuilder.ToString()}\n[첨부된 코드 내용]{codeContentBuilder.ToString()}\n[사용자 질문]\n{userPrompt}";
                }

                finalUserPromptToSend += "\n\n[매우 중요한 시스템 지시사항]\n당신은 코드를 작성할 때 모든 파일의 전체 코드를 각각 작성해야 합니다.\n새로운 파일을 만들거나 여러 파일을 수정할 경우, 반드시 각 파일마다 별도의 태그를 사용하여 코드를 분리해서 출력해 주세요. (예시:\n[FILE_START: 파일A.cs]\nusing UnityEngine;\n// 파일A 코드...\n[FILE_END: 파일A.cs]\n\n[FILE_START: 파일B.cs]\nusing UnityEngine;\n// 파일B 코드...\n[FILE_END: 파일B.cs])\n*주의: 마크다운 코드 파싱을 위해, 저 태그 문자열이 반드시 코드의 시작과 끝에 있어야 합니다. 태그 안쪽에는 마크다운 제목(# 파일명)이나 기타 설명 텍스트를 절대 넣지 말고, 오직 순수한 C# 컴파일이 가능한 코드만 작성하세요.";
            }

            string selectedEndpoint = modelEndpoints[selectedModelIndex];
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
                        role = "user",
                        parts = new RequestPartData[] { new RequestPartData { text = finalUserPromptToSend } }
                    }
                }
            };

            string jsonData = JsonUtility.ToJson(requestObject);

            using (BypassCertificate cert = new BypassCertificate())
            using (UnityWebRequest webRequest = new UnityWebRequest(url, "POST"))
            {
                byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonData);
                webRequest.uploadHandler = new UploadHandlerRaw(bodyRaw);
                webRequest.downloadHandler = new DownloadHandlerBuffer();

                webRequest.SetRequestHeader("Authorization", $"Bearer {accessToken}");
                webRequest.SetRequestHeader("Content-Type", "application/json; charset=utf-8");

                webRequest.timeout = 300;
                webRequest.certificateHandler = cert;

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
                        lastAIResponse = finalAnswer;
                        responseText = finalAnswer;

                        if (autoSaveLog) SaveLogToFile(finalUserPromptToSend, finalAnswer);

                        ProcessAutoOverwrite(finalAnswer, isAutoDebug);
                    }
                    else
                    {
                        responseText = "데이터를 분석할 수 없습니다.\n" + webRequest.downloadHandler.text;
                    }
                }
                else
                {
                    if (webRequest.error.Contains("timeout") || webRequest.error.Contains("Timeout"))
                    {
                        responseText = "통신 시간 초과 (Timeout): 코드가 너무 길거나 서버가 혼잡하여 응답이 지연되었습니다. 다시 생성하기를 눌러주세요.";
                    }
                    else
                    {
                        responseText = "통신 에러: " + webRequest.error + "\n" + webRequest.downloadHandler.text;
                    }

                    isWaitingForCompilation = false;
                }
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
    private void ProcessAutoOverwrite(string responseBody, bool isAutoDebug)
    {
        Queue<PendingDiffData> pendingDiffs = new Queue<PendingDiffData>();
        bool tagFound = false;

        // 1. 첨부된 파일 목록 기준이 아니라, 답변에 있는 모든 [FILE_START: 파일명.cs] 블록을 한 번에 다 찾습니다.
        // [핵심 수정] 괄호 앞에 '?:'를 붙여서 추출 그룹 인덱스가 밀리는 버그를 해결합니다!
        string pattern = @"\[?FILE_START:\s*(.*?\.(?:cs|json|txt|inputactions|prefab|md))\]?([\s\S]*?)\[?FILE_END:\s*\1\]?";
        var matches = System.Text.RegularExpressions.Regex.Matches(responseBody, pattern);

        if (matches.Count > 0)
        {
            tagFound = true;
            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                string fileName = match.Groups[1].Value.Trim();
                string extractedCode = match.Groups[2].Value.Trim();

                // 1차: 마크다운 코드 블록 기호 제거 (```cs 등 다양한 포맷 완벽 대응)
                extractedCode = extractedCode.Replace("```csharp", "").Replace("```cs", "").Replace("```json", "").Replace("```", "").Trim();

                // 2차: AI가 몰래 집어넣은 마크다운 제목 강제 청소 (Sanitizer)
                string[] lines = extractedCode.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                List<string> cleanLines = new List<string>();
                foreach (string line in lines)
                {
                    string trimmedLine = line.TrimStart();
                    if (trimmedLine.StartsWith("#"))
                    {
                        if (!trimmedLine.StartsWith("#if") && !trimmedLine.StartsWith("#else") &&
                            !trimmedLine.StartsWith("#elif") && !trimmedLine.StartsWith("#endif") &&
                            !trimmedLine.StartsWith("#define") && !trimmedLine.StartsWith("#undef") &&
                            !trimmedLine.StartsWith("#region") && !trimmedLine.StartsWith("#endregion") &&
                            !trimmedLine.StartsWith("#error") && !trimmedLine.StartsWith("#warning") &&
                            !trimmedLine.StartsWith("#line") && !trimmedLine.StartsWith("#pragma"))
                        {
                            continue;
                        }
                    }
                    cleanLines.Add(line);
                }
                extractedCode = string.Join("\n", cleanLines).Trim();

                // 2. 이 파일이 첨부된 기존 파일인지, 완전히 새로 만들어야 하는 파일인지 검사합니다.
                string assetPath = "";
                string originalContent = "";
                bool isNewFile = true;
                bool shouldIgnore = false; // [추가됨] 무시해야 할 파일인지 판별하는 플래그

                foreach (var slot in targetScripts)
                {
                    if (slot.script != null && (slot.script.name + ".cs" == fileName))
                    {
                        if (slot.isModifiable)
                        {
                            // 상태 1: 사용자가 첨부했고, 수정도 허락함
                            assetPath = AssetDatabase.GetAssetPath(slot.script);
                            originalContent = File.ReadAllText(assetPath);
                            isNewFile = false;
                        }
                        else
                        {
                            // 상태 2: 사용자가 첨부했지만, 수정을 막아둠 (체크 해제)
                            shouldIgnore = true;
                        }
                        break;
                    }
                }

                // [핵심] 사용자가 체크를 해제한 파일이라면, 새 파일로 만들지 않고 아예 큐(Queue)에서 건너뜁니다!
                if (shouldIgnore) continue;

                // 3. 첨부 목록에 아예 없는 완전 새 파일이라면 폴더를 지정해 줍니다.
                if (isNewFile)
                {
                    string defaultDir = "Assets/_Code/GeminiGenerated"; // 임시 저장소
                    if (!Directory.Exists(defaultDir)) Directory.CreateDirectory(defaultDir);

                    assetPath = Path.Combine(defaultDir, fileName);

                    // [개선] 이미 GeminiGenerated 폴더에 만들어진 파일이라면 덮어쓰기 전에 기존 내용을 읽어옵니다. (Diff 창 정상 출력을 위해)
                    if (File.Exists(assetPath))
                    {
                        originalContent = File.ReadAllText(assetPath);
                    }
                    else
                    {
                        originalContent = "// [AI가 새로 생성할 파일입니다]\n// 파일 경로: " + assetPath + "\n";
                    }
                }

                pendingDiffs.Enqueue(new PendingDiffData { assetPath = assetPath, originalContent = originalContent, newContent = extractedCode });
            }
        }

        // 파싱이 끝난 후 큐에 검토할 파일이 있다면 첫 번째 창을 띄웁니다.
        if (pendingDiffs.Count > 0)
        {
            ShowNextDiff(pendingDiffs, isAutoDebug);
        }
        else if (isAutoDebug && !tagFound)
        {
            // 태그를 하나도 찾지 못했을 경우 재시도 루프
            if (currentRetryCount < MAX_RETRIES)
            {
                currentRetryCount++;
                currentCompileErrors.Clear();
                currentCompileErrors.Add($"에러: 답변에서 [FILE_START: 파일명.cs] 및 [FILE_END: 파일명.cs] 태그를 전혀 찾을 수 없습니다. 서론을 생략하고 반드시 태그 안에 전체 코드를 넣어서 다시 보내주세요.");
                Debug.LogWarning($"[Gemini Assistant] 태그 파싱 실패. AI에게 재작성을 요청합니다. ({currentRetryCount}/{MAX_RETRIES})");
                TriggerAutoDebug();
            }
            else
            {
                responseText = "최대 재시도 횟수를 초과했습니다. AI가 규칙을 지키지 않아 자동 수정에 실패했습니다.";
                Repaint();
            }
        }
    }
    private void ShowNextDiff(Queue<PendingDiffData> queue, bool isAutoDebug)
    {
        // 큐가 비워졌다면 모든 파일의 검토가 끝난 것이므로, 여기서 최종적으로 컴파일을 1번만 실행합니다.
        if (queue.Count == 0)
        {
            isWaitingForCompilation = true;
            currentCompileErrors.Clear();
            responseText = "모든 코드 파일의 검토가 완료되었습니다. 컴파일 검증 중... ⏳";
            Repaint();
            AssetDatabase.Refresh();
            return;
        }

        // 큐에서 맨 앞에 있는 파일을 하나 꺼냅니다.
        var diff = queue.Dequeue();

        GeminiDiffWindow.ShowWindow(diff.originalContent, diff.newContent, diff.assetPath,
            (finalCode) => {
                // 사용자가 적용을 눌렀을 때
                File.WriteAllText(diff.assetPath, finalCode, Encoding.UTF8);
                Debug.Log($"[Gemini Assistant] {Path.GetFileName(diff.assetPath)} 파일 업데이트 완료.");
                ShowNextDiff(queue, isAutoDebug); // 재귀 호출로 다음 파일을 띄움
            },
            () => {
                // 사용자가 취소나 X버튼을 눌렀을 때
                Debug.Log($"[Gemini Assistant] {Path.GetFileName(diff.assetPath)} 파일 적용이 취소되었습니다.");
                ShowNextDiff(queue, isAutoDebug); // 덮어쓰기 없이 다음 파일을 띄움
            });
    }

    // --- [자동 디버깅 핵심 함수 3개 추가] ---

    // 1. 개별 어셈블리 컴파일 완료 시 에러 수집
    private void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
    {
        if (!isWaitingForCompilation) return;

        foreach (var msg in messages)
        {
            if (msg.type == CompilerMessageType.Error)
            {
                // 줄 번호와 에러 메시지를 수집
                currentCompileErrors.Add($"- {msg.file} (Line {msg.line}): {msg.message}");
            }
        }
    }

    // 2. 전체 컴파일 파이프라인 종료 시 루프 결정
    private void OnCompilationFinished(object obj)
    {
        if (!isWaitingForCompilation) return;
        isWaitingForCompilation = false;

        if (currentCompileErrors.Count > 0)
        {
            if (currentRetryCount < MAX_RETRIES)
            {
                currentRetryCount++;
                Debug.LogWarning($"[Gemini Assistant] 컴파일 에러 감지됨. AI에게 자동 수정을 요청합니다. (재시도 {currentRetryCount}/{MAX_RETRIES})");
                TriggerAutoDebug();
            }
            else
            {
                responseText = $"⚠️ 최대 자동 디버깅 횟수({MAX_RETRIES}회)를 초과했습니다. 수동으로 에러를 확인해주세요.\n\n[에러 내역]\n" + string.Join("\n", currentCompileErrors);
                Repaint();
            }
        }
        else
        {
            Debug.Log("[Gemini Assistant] 컴파일 성공! 에러가 없습니다.");
            responseText = "✅ 성성공적으로 코드가 작성되고 에러 없이 컴파일되었습니다!\n\n[AI 원본 응답 내역]\n" + lastAIResponse;
            Repaint();
        }
    }

    // 3. 재요청 트리거
    private void TriggerAutoDebug()
    {
        CallGeminiAPI(true);
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
            Debug.Log($"[Gemini Assistant] 설정이 성공적으로 저장되었습니다.");
        }
        catch (Exception e) { Debug.LogError($"[Gemini Assistant] 설정 저장 실패: {e.Message}"); }
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
                systemPrompt = "[핵심 기술 스택 및 환경]\n1. 렌더링 파이프라인: URP\n2. 네트워크 프레임워크: NGO";
                SaveSettingsToFile();
            }
        }
        catch (Exception e) { Debug.LogError($"[Gemini Assistant] 설정 불러오기 실패: {e.Message}"); }
    }

    private void SaveLogToFile(string promptToSave, string responseToSave)
    {
        try
        {
            if (!Directory.Exists(logDirectoryPath)) Directory.CreateDirectory(logDirectoryPath);
            string filePath = Path.Combine(logDirectoryPath, $"Gemini_Log_{DateTime.Now.ToString("yyyyMMdd")}.txt");

            StringBuilder logBuilder = new StringBuilder();
            logBuilder.AppendLine("==================================================");
            logBuilder.AppendLine($"[Time: {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")}]");
            logBuilder.AppendLine($"[Model: {modelDisplayNames[selectedModelIndex]}]");
            logBuilder.AppendLine("==================================================");
            logBuilder.AppendLine(promptToSave);
            logBuilder.AppendLine("\n--------------------------------------------------");
            logBuilder.AppendLine("[Response]");
            logBuilder.AppendLine(responseToSave);
            logBuilder.AppendLine("\n\n");

            File.AppendAllText(filePath, logBuilder.ToString(), Encoding.UTF8);
            AssetDatabase.Refresh();
        }
        catch (Exception e) { Debug.LogError($"[Gemini Assistant] 로그 저장 실패: {e.Message}"); }
    }

    private void OpenLogFolder()
    {
        if (!Directory.Exists(logDirectoryPath)) { Directory.CreateDirectory(logDirectoryPath); AssetDatabase.Refresh(); }
        EditorUtility.RevealInFinder(logDirectoryPath);
    }
}

// =================================================================
// 수신 데이터 파싱용 기존 Response 클래스들
// =================================================================
[System.Serializable]
public class GeminiResponse { public Candidate[] candidates; }

[System.Serializable]
public class Candidate { public Content content; }

[System.Serializable]
public class Content { public Part[] parts; }

[System.Serializable]
public class Part { public string text; }