using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using NaukriMatcher.Api.Models;

namespace NaukriMatcher.Api.Services;

public class GeminiService : IGeminiService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GeminiService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public GeminiService(HttpClient httpClient, IConfiguration configuration, ILogger<GeminiService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<List<EvaluationResult>> EvaluateCandidatesAsync(
        JobDescriptionDto jobDescription,
        List<CandidateCardDto> candidates,
        CancellationToken cancellationToken = default)
    {
        if (candidates == null || candidates.Count == 0)
        {
            return new List<EvaluationResult>();
        }

        string apiKey = _configuration["Gemini:ApiKey"]
            ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY")
            ?? string.Empty;

        // If API key is not configured or is placeholder, provide intelligent local fallback evaluation
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Contains("YOUR_GEMINI_API_KEY", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Gemini API key is not configured. Running fallback rule-based evaluation.");
            return EvaluateLocally(jobDescription, candidates);
        }

        string model = _configuration["Gemini:Model"] ?? "gemini-2.5-flash";

        try
        {
            var systemInstructionText = """
                You are an expert Technical Recruitment Assistant evaluating candidate search cards from Naukri.com against a Job Description.
                Analyze each candidate profile strictly and PURELY based on the following four foundational criteria:
                1. Hands-on Experience: Total years and depth of relevant hands-on engineering experience in production (derived from snippet and experience metrics).
                2. The Skills: Concrete match against mandatory and core technical skills required by the JD.
                3. The Role in which the candidate has worked already: Relevance and seniority of the candidate's current and previous roles (designation) compared to the target role.
                4. The Companies the candidate has worked: The candidate's company background (industry relevance, company tier/repute, tech caliber, career stability).

                CRITICAL SCORING & LENIENCY RULES:
                - The matchPercentage (0 to 100) MUST be determined PURELY by the 4 pillars:
                  * Hands-on Experience (30% weight)
                  * Skills (35% weight)
                  * Role in which candidate has worked already (20% weight)
                  * Companies the candidate has worked (15% weight)
                - CARD BREVITY & LENIENCY RULE (VERY IMPORTANT):
                  * Candidate search cards on Naukri contain concise, truncated snippets, limited tag chips, and frequently omit company or complete work history details.
                  * IF REQUIRED DETAILS (e.g. specific mandatory skills, detailed project evidence, company names, or explicit experience tenure) ARE NOT FULLY PRESENT ON THE SEARCH CARD:
                    BE LENIENT WITH THE SCORE. Do not aggressively penalize or knock out candidates simply because the search card has limited space.
                    Give candidates the benefit of the doubt and assign a generous, moderate score (typically in the 55% - 72% "MAYBE" range) unless there is clear evidence of a direct negative mismatch.
                  * Reserve "SKIP" (< 50%) strictly for clear, fundamental contradictions (e.g. an exclusively Java/PHP developer when .NET C# is required, or completely unrelated non-technical roles).
                - NOTICE PERIOD, CURRENT SALARY, AND LOCATION MUST NEVER INFLUENCE, PENALIZE, OR INFLATE THE SCORE.
                - Anti-Hallucination & Notice Period Recruiter Note:
                  * If candidate's noticePeriod is "Not specified", empty, or unknown: explicitly state "Notice period: Not specified" in reasoning.
                  * If notice period is stated: state it in the reasoning (e.g. "Notice period: 15 days" or "Notice period: 90 days") for recruiter operational awareness ONLY.
                  * NEVER penalize, knock out, or reward a candidate's score based on notice period.

                Recommendation Categories (Must be exactly one of these):
                - "WORTH OPENING": Strong match (score >= 75%), possesses high hands-on experience, core skills, role alignment, and solid company background.
                - "MAYBE": Moderate match (score 50%-74%), partial skill/experience overlap, slight role/company divergence, or limited card details warranting full profile review.
                - "SKIP": Poor match (score < 50%), clear mismatch in core technical stack or unrelated role.

                Output Requirement:
                Return a strictly valid JSON object matching this schema:
                {
                  "evaluations": [
                    {
                      "candidateId": "string",
                      "matchPercentage": 85,
                      "recommendation": "WORTH OPENING",
                      "reasoning": "Concise 1-2 sentence justification highlighting hands-on experience, skills, roles worked, and companies, followed by notice period note (e.g., 'Notice period: ...')."
                    }
                  ]
                }
                Do not include markdown fences outside the JSON. Return pure JSON only.
                """;

            var promptData = new
            {
                job = new
                {
                    title = jobDescription.Title,
                    rawDescription = jobDescription.RawText,
                    mandatorySkills = jobDescription.MandatorySkills,
                    maxNoticePeriodDays = jobDescription.MaxNoticePeriodDays
                },
                candidates = candidates.Select(c => new
                {
                    candidateId = c.CandidateId,
                    designation = c.Designation,
                    company = c.Company,
                    totalExperience = c.TotalExperience,
                    keySkills = c.KeySkills,
                    noticePeriod = c.NoticePeriod,
                    location = c.Location,
                    annualSalary = c.AnnualSalary,
                    snippet = c.Snippet
                }).ToList()
            };

            var userPromptText = $"Please evaluate the following candidates against the job description:\n\n{JsonSerializer.Serialize(promptData, JsonOptions)}";

            var geminiRequest = new
            {
                systemInstruction = new
                {
                    parts = new[] { new { text = systemInstructionText } }
                },
                contents = new[]
                {
                    new
                    {
                        role = "user",
                        parts = new[] { new { text = userPromptText } }
                    }
                },
                generationConfig = new
                {
                    temperature = 0.2,
                    responseMimeType = "application/json"
                }
            };

            var requestUrl = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";
            var jsonContent = new StringContent(JsonSerializer.Serialize(geminiRequest, JsonOptions), Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(requestUrl, jsonContent, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Gemini API call failed with status {StatusCode}: {ErrorBody}", response.StatusCode, errorBody);

                // Fallback to local evaluation on API errors so user workflow isn't blocked
                return EvaluateLocally(jobDescription, candidates, $"[Gemini API Error {response.StatusCode} - Fallback]");
            }

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParseGeminiResponse(responseBody, candidates);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception while contacting Gemini API. Reverting to fallback evaluation.");
            return EvaluateLocally(jobDescription, candidates, "[Gemini Connection Failed - Fallback]");
        }
    }

    private List<EvaluationResult> ParseGeminiResponse(string rawJson, List<CandidateCardDto> originalCandidates)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("candidates", out var candidateArray) && candidateArray.GetArrayLength() > 0)
            {
                var firstCandidate = candidateArray[0];
                if (firstCandidate.TryGetProperty("content", out var content) &&
                    content.TryGetProperty("parts", out var parts) &&
                    parts.GetArrayLength() > 0)
                {
                    var partText = parts[0].GetProperty("text").GetString();
                    if (!string.IsNullOrWhiteSpace(partText))
                    {
                        var cleaned = CleanJsonString(partText);

                        using var parsedContent = JsonDocument.Parse(cleaned);
                        if (parsedContent.RootElement.TryGetProperty("evaluations", out var evaluationsElement))
                        {
                            var results = JsonSerializer.Deserialize<List<EvaluationResult>>(evaluationsElement.GetRawText(), JsonOptions);
                            if (results != null && results.Count > 0)
                            {
                                return AlignWithCandidates(results, originalCandidates);
                            }
                        }
                        else if (parsedContent.RootElement.ValueKind == JsonValueKind.Array)
                        {
                            var results = JsonSerializer.Deserialize<List<EvaluationResult>>(cleaned, JsonOptions);
                            if (results != null && results.Count > 0)
                            {
                                return AlignWithCandidates(results, originalCandidates);
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse Gemini output JSON structure. Raw: {RawJson}", rawJson);
        }

        return EvaluateLocally(new JobDescriptionDto("", "", new List<string>(), 0), originalCandidates, "[Parsing Fallback]");
    }

    private static string CleanJsonString(string text)
    {
        text = text.Trim();
        if (text.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
        {
            text = text[7..];
        }
        else if (text.StartsWith("```"))
        {
            text = text[3..];
        }

        if (text.EndsWith("```"))
        {
            text = text[..^3];
        }

        return text.Trim();
    }

    private static List<EvaluationResult> AlignWithCandidates(List<EvaluationResult> results, List<CandidateCardDto> candidates)
    {
        var resultMap = results.ToDictionary(r => r.CandidateId, r => r, StringComparer.OrdinalIgnoreCase);
        var finalResults = new List<EvaluationResult>();

        foreach (var c in candidates)
        {
            if (resultMap.TryGetValue(c.CandidateId, out var evaluated))
            {
                finalResults.Add(evaluated);
            }
            else
            {
                finalResults.Add(new EvaluationResult(
                    c.CandidateId,
                    50,
                    "MAYBE",
                    "Evaluated with partial profile match."
                ));
            }
        }

        return finalResults;
    }

    private static List<EvaluationResult> EvaluateLocally(JobDescriptionDto job, List<CandidateCardDto> candidates, string prefix = "")
    {
        var results = new List<EvaluationResult>();
        var mandatory = job.MandatorySkills ?? new List<string>();

        foreach (var candidate in candidates)
        {
            var reasons = new List<string>();

            // Pillar 1: Hands-on Experience (0 - 30 pts) [Lenient when experience details are brief/unspecified on card]
            int experienceScore;
            double years = ExtractYearsOfExperience(candidate.TotalExperience);
            var candidateSnippetLower = (candidate.Snippet ?? "").ToLowerInvariant();

            bool hasHandsOnIndicators = candidateSnippetLower.Contains("architect") ||
                                        candidateSnippetLower.Contains("built") ||
                                        candidateSnippetLower.Contains("deployed") ||
                                        candidateSnippetLower.Contains("develop") ||
                                        candidateSnippetLower.Contains("implement") ||
                                        candidateSnippetLower.Contains("microservices") ||
                                        candidateSnippetLower.Contains("production");

            if (years >= 5)
            {
                experienceScore = hasHandsOnIndicators ? 30 : 26;
                reasons.Add($"Hands-on exp: {candidate.TotalExperience} ({experienceScore}/30 pts)");
            }
            else if (years >= 3)
            {
                experienceScore = hasHandsOnIndicators ? 26 : 22;
                reasons.Add($"Hands-on exp: {candidate.TotalExperience} ({experienceScore}/30 pts)");
            }
            else if (years >= 1)
            {
                experienceScore = 19;
                reasons.Add($"Hands-on exp: {candidate.TotalExperience} ({experienceScore}/30 pts)");
            }
            else
            {
                // Experience not specified or limited on search card -> lenient baseline
                experienceScore = 18;
                reasons.Add("Hands-on exp: Limited/unspecified on card (lenient 18/30 pts)");
            }

            // Pillar 2: Skills Match (0 - 35 pts) [Lenient when search card tag list is truncated]
            int skillsScore;
            int matchedSkills = 0;
            var candidateAllText = $"{candidate.Designation} {string.Join(" ", candidate.KeySkills ?? new())} {candidate.Snippet} {candidate.Company}".ToLowerInvariant();

            foreach (var skill in mandatory.Where(s => !string.IsNullOrWhiteSpace(s)))
            {
                if (candidateAllText.Contains(skill.Trim().ToLowerInvariant()))
                {
                    matchedSkills++;
                }
            }

            // Detect blatant technical mismatch (e.g. pure Java/Spring or PHP when .NET C# is required)
            bool isClearStackMismatch = (candidateAllText.Contains("java") || candidateAllText.Contains("spring") || candidateAllText.Contains("php")) 
                                        && !candidateAllText.Contains(".net") && !candidateAllText.Contains("c#");

            if (mandatory.Count > 0)
            {
                if (isClearStackMismatch && matchedSkills == 0)
                {
                    skillsScore = 6;
                    reasons.Add($"Skills: Tech stack divergence - no target skills evident ({skillsScore}/35 pts)");
                }
                else
                {
                    // Leniency: on search cards, only a subset of skills fit in chips. Give generous benefit of the doubt.
                    double skillRatio = (double)matchedSkills / mandatory.Count;
                    if (matchedSkills >= mandatory.Count)
                    {
                        skillsScore = 35;
                    }
                    else if (matchedSkills > 0)
                    {
                        skillsScore = (int)Math.Round(18 + (skillRatio * 17));
                    }
                    else
                    {
                        // 0 matched skills in brief snippet, but not a clear mismatch -> lenient baseline
                        skillsScore = 16;
                    }
                    reasons.Add($"Skills: Matched {matchedSkills}/{mandatory.Count} mandatory skills on card ({skillsScore}/35 pts)");
                }
            }
            else
            {
                skillsScore = 28;
                reasons.Add($"Skills: General match ({skillsScore}/35 pts)");
            }

            // Pillar 3: Role in which candidate has worked already (0 - 20 pts) [Lenient when designation is brief]
            int roleScore;
            var desigLower = (candidate.Designation ?? "").ToLowerInvariant();
            var jobTitleLower = (job.Title ?? "").ToLowerInvariant();

            if (!string.IsNullOrWhiteSpace(desigLower) && !desigLower.Equals("candidate", StringComparison.OrdinalIgnoreCase))
            {
                var roleKeywords = new[] { "lead", "architect", "senior", "sr", "backend", "full stack", "developer", "engineer", "software" };
                int roleMatches = roleKeywords.Count(k => desigLower.Contains(k) && jobTitleLower.Contains(k));

                if (roleMatches >= 2 || (jobTitleLower.Length > 0 && desigLower.Contains(jobTitleLower)))
                {
                    roleScore = 20;
                    reasons.Add($"Role: Strong alignment as '{candidate.Designation}' (20/20 pts)");
                }
                else if (roleMatches == 1 || desigLower.Contains("developer") || desigLower.Contains("engineer") || desigLower.Contains("programmer"))
                {
                    roleScore = 16;
                    reasons.Add($"Role: Relevant engineering title '{candidate.Designation}' (16/20 pts)");
                }
                else if (isClearStackMismatch)
                {
                    roleScore = 8;
                    reasons.Add($"Role: Divergent title '{candidate.Designation}' (8/20 pts)");
                }
                else
                {
                    roleScore = 13; // Lenient
                    reasons.Add($"Role: Partially aligned title '{candidate.Designation}' (lenient 13/20 pts)");
                }
            }
            else
            {
                // Designation omitted or generic on card -> lenient baseline
                roleScore = 14;
                reasons.Add("Role: Designation not fully specified on card (lenient 14/20 pts)");
            }

            // Pillar 4: Companies the candidate has worked (0 - 15 pts) [Lenient when company omitted from card]
            int companyScore;
            var companyText = $"{candidate.Company} {candidate.Snippet}".ToLowerInvariant();
            bool hasReputableDomain = companyText.Contains("fintech") ||
                                      companyText.Contains("tech") ||
                                      companyText.Contains("solutions") ||
                                      companyText.Contains("systems") ||
                                      companyText.Contains("global") ||
                                      companyText.Contains("cloud") ||
                                      companyText.Contains("pvt") ||
                                      companyText.Contains("ltd") ||
                                      companyText.Contains("bank") ||
                                      companyText.Contains("enterprise") ||
                                      companyText.Contains("consulting");

            if (!string.IsNullOrWhiteSpace(candidate.Company))
            {
                companyScore = hasReputableDomain ? 15 : 13;
                reasons.Add($"Company: '{candidate.Company}' ({companyScore}/15 pts)");
            }
            else if (hasReputableDomain)
            {
                companyScore = 13;
                reasons.Add($"Company: Tech domain mentioned in snippet ({companyScore}/15 pts)");
            }
            else
            {
                // Missing company details on search card -> lenient baseline
                companyScore = 12;
                reasons.Add("Company: Not specified on card (lenient 12/15 pts)");
            }

            // Pure 4-Pillar Score Calculation (Total Max 100)
            int score = experienceScore + skillsScore + roleScore + companyScore;
            score = Math.Clamp(score, 15, 98);

            // Notice period is informational ONLY - does NOT affect the score
            string noticeDisplay = string.IsNullOrWhiteSpace(candidate.NoticePeriod) || candidate.NoticePeriod.Contains("not specified", StringComparison.OrdinalIgnoreCase)
                ? "Not specified"
                : candidate.NoticePeriod;
            reasons.Add($"[Notice period: {noticeDisplay} (Informational)]");

            string recommendation = score >= 75 ? "WORTH OPENING"
                : score >= 50 ? "MAYBE"
                : "SKIP";

            string combinedReasoning = string.Join("; ", reasons);
            if (!string.IsNullOrEmpty(prefix))
            {
                combinedReasoning = $"{prefix} {combinedReasoning}";
            }

            results.Add(new EvaluationResult(
                candidate.CandidateId,
                score,
                recommendation,
                combinedReasoning
            ));
        }

        return results;
    }

    public async Task<ProfileAnalysisResult> AnalyzeDetailedProfileAsync(
        JobDescriptionDto jobDescription,
        DetailedCandidateProfileDto profile,
        CancellationToken cancellationToken = default)
    {
        if (profile == null)
        {
            return new ProfileAnalysisResult(
                "", "Unknown", 0, "DO NOT PROCEED", "No profile data provided.",
                new List<TechUsageAudit>(), new List<string>(), new List<string> { "Empty profile data." }, new List<InterviewQuestionDto>()
            );
        }

        string apiKey = _configuration["Gemini:ApiKey"]
            ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY")
            ?? string.Empty;

        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Contains("YOUR_GEMINI_API_KEY", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Gemini API key is not configured. Running fallback detailed profile audit.");
            return AnalyzeProfileLocally(jobDescription, profile);
        }

        string model = _configuration["Gemini:Model"] ?? "gemini-2.5-flash";

        try
        {
            var systemInstructionText = """
                You are a Principal Technical Recruiter and Engineering Architect conducting a rigorous Deep Career Audit on a candidate's full profile and resume against a Job Description.

                YOUR PRIMARY OBJECTIVE:
                Audit the candidate's genuine career suitability and calculate an overallSuitabilityScore (0 to 100) based PURELY on the following four foundational pillars:
                1. Hands-on Experience:
                   - Verifiable hands-on project execution across their work history (daily responsibilities, project descriptions, microservices, architecture, genuine production usage vs superficial keyword stuffing).
                   - Determine for each required tech: Did they actually build, maintain, or architect systems with this technology?
                   - Assign DepthLevel: "Primary / Production" (core day-to-day tool), "Secondary / Ancillary" (used occasionally), or "Keyword Stuffed / Not Evident".
                2. The Skills:
                   - Concrete coverage, depth, and mastery of mandatory and required technical skills demonstrated in actual production environments.
                3. The Role in which the candidate has worked already:
                   - Past and current roles/designations held across career history, career progression (e.g. Developer -> Senior -> Lead), and alignment with the target role.
                4. The Companies the candidate has worked:
                   - The caliber, domain relevance (e.g. FinTech, SaaS, Banking, Enterprise, Product, Tech Consulting), stability, and credibility of the companies the candidate has worked at.

                CRITICAL SCORING RULES:
                - overallSuitabilityScore (0-100) MUST be determined PURELY by:
                  * Hands-on Experience (35% weight)
                  * Skills (30% weight)
                  * Role in which candidate has worked already (20% weight)
                  * Companies the candidate has worked (15% weight)
                - NOTICE PERIOD, CURRENT SALARY, AND LOCATION MUST NEVER INFLUENCE, PENALIZE, OR INFLATE THE SCORE.
                - Anti-Hallucination & Notice Period Recruiter Reporting:
                  * In the executive summary or strengths, note the candidate's stated notice period (or "Notice period: Not specified" if absent) strictly for recruiter scheduling awareness.
                  * NEVER deduct points, penalize, or knock out candidates due to notice period.

                Final Verdict Recommendation (Must be one of):
                - "WORTH GOING FORWARD": Strong evidence of authentic hands-on usage of core tech, solid role progression, relevant company background (score >= 75%).
                - "BORDERLINE / INTERVIEW WITH CAUTION": Moderate hands-on depth, or partial skills match, or minor role/company divergence (score 50%-74%).
                - "DO NOT PROCEED": Core required tech is keyword stuffed without real project evidence, or mismatched role/experience (score < 50%).

                Targeted HR Interview Questions & Answers (EXACTLY 5 Questions):
                Generate exactly 5 tailored interview questions based directly on the candidate's actual projects, claims, and technologies from their resume.
                IMPORTANT: The HR interviewer who will ask these questions is NOT a technical person. Therefore:
                - For each question, provide an "expectedAnswer" written in clear, plain English (2-3 sentences) so the non-technical HR recruiter can understand what a solid, knowledgeable response sounds like.
                - Provide "expectedKeywords": an array of 3-5 specific technical buzzwords or industry terms that the candidate should naturally say during their answer. The HR recruiter will listen for these keywords during the call.
                - Question breakdown (strictly 2 Simple and 3 Advanced):
                  * Question 1: difficulty = "Simple" (Tests core day-to-day fundamentals and practical usage of their primary mandatory technology).
                  * Question 2: difficulty = "Simple" (Tests practical API design, tooling, or project workflow from their recent employment).
                  * Question 3: difficulty = "Advanced" (Tests system architecture, microservices, scalability, or distributed systems from their stated projects).
                  * Question 4: difficulty = "Advanced" (Tests database optimization, query tuning, concurrency, or caching from their work history).
                  * Question 5: difficulty = "Advanced" (Tests diagnosing a difficult production bug, handling outages, or architectural trade-offs).

                Output Format (Strict JSON only, no markdown wrappers):
                {
                  "candidateId": "string",
                  "candidateName": "string",
                  "overallSuitabilityScore": 85,
                  "recommendation": "WORTH GOING FORWARD",
                  "executiveSummary": "2-3 sentences summarizing their hands-on experience, skills, roles worked already, and companies worked, followed by notice period note.",
                  "techAudits": [
                    {
                      "technology": "string",
                      "actuallyUsedInCareer": true,
                      "careerEvidence": "Used across 2 companies (3.5 years) developing REST microservices.",
                      "depthLevel": "Primary / Production"
                    }
                  ],
                  "strengths": ["string", "string"],
                  "redFlags": ["string", "string"],
                  "interviewQuestions": [
                    {
                      "question": "Clear question for HR to ask",
                      "difficulty": "Simple",
                      "expectedAnswer": "Plain-English explanation of what a good candidate should say so non-technical HR can verify.",
                      "expectedKeywords": ["Keyword1", "Keyword2", "Keyword3"]
                    }
                  ]
                }
                """;

            var promptData = new
            {
                targetJob = new
                {
                    title = jobDescription.Title,
                    rawRequirements = jobDescription.RawText,
                    mandatorySkills = jobDescription.MandatorySkills,
                    maxNoticePeriodDays = jobDescription.MaxNoticePeriodDays
                },
                candidateProfile = new
                {
                    candidateId = profile.CandidateId,
                    fullName = profile.FullName,
                    currentDesignation = profile.CurrentDesignation,
                    totalExperience = profile.TotalExperience,
                    noticePeriod = profile.NoticePeriod,
                    currentLocation = profile.CurrentLocation,
                    annualSalary = profile.AnnualSalary,
                    keySkills = profile.KeySkills,
                    workHistory = profile.Experiences?.Select(e => new
                    {
                        company = e.Company,
                        designation = e.Designation,
                        duration = e.Duration,
                        projectResponsibilities = e.Description
                    }).ToList(),
                    resumeHeadline = profile.ResumeHeadline,
                    fullRawProfileText = profile.FullProfileRawText?.Length > 4000 ? profile.FullProfileRawText[..4000] : profile.FullProfileRawText
                }
            };

            var userPromptText = $"Please conduct a deep career audit on this candidate's profile:\n\n{JsonSerializer.Serialize(promptData, JsonOptions)}";

            var geminiRequest = new
            {
                systemInstruction = new
                {
                    parts = new[] { new { text = systemInstructionText } }
                },
                contents = new[]
                {
                    new
                    {
                        role = "user",
                        parts = new[] { new { text = userPromptText } }
                    }
                },
                generationConfig = new
                {
                    temperature = 0.2,
                    responseMimeType = "application/json"
                }
            };

            var requestUrl = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";
            var jsonContent = new StringContent(JsonSerializer.Serialize(geminiRequest, JsonOptions), Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(requestUrl, jsonContent, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Gemini profile analysis failed with status {StatusCode}: {ErrorBody}", response.StatusCode, errorBody);
                return AnalyzeProfileLocally(jobDescription, profile, $"[Gemini API Error {response.StatusCode} - Fallback]");
            }

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParseProfileAnalysisResponse(responseBody, jobDescription, profile);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception contacting Gemini API for profile analysis. Reverting to fallback audit.");
            return AnalyzeProfileLocally(jobDescription, profile, "[Gemini Connection Failed - Fallback]");
        }
    }

    private ProfileAnalysisResult ParseProfileAnalysisResponse(string rawJson, JobDescriptionDto jobDescription, DetailedCandidateProfileDto profile)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("candidates", out var candidateArray) && candidateArray.GetArrayLength() > 0)
            {
                var firstCandidate = candidateArray[0];
                if (firstCandidate.TryGetProperty("content", out var content) &&
                    content.TryGetProperty("parts", out var parts) &&
                    parts.GetArrayLength() > 0)
                {
                    var partText = parts[0].GetProperty("text").GetString();
                    if (!string.IsNullOrWhiteSpace(partText))
                    {
                        var cleaned = CleanJsonString(partText);
                        try
                        {
                            var result = JsonSerializer.Deserialize<ProfileAnalysisResult>(cleaned, JsonOptions);
                            if (result != null && result.InterviewQuestions != null && result.InterviewQuestions.Count > 0)
                            {
                                return result with
                                {
                                    CandidateId = string.IsNullOrWhiteSpace(result.CandidateId) ? profile.CandidateId : result.CandidateId,
                                    CandidateName = string.IsNullOrWhiteSpace(result.CandidateName) ? profile.FullName : result.CandidateName
                                };
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Direct deserialize of ProfileAnalysisResult failed; attempting fallback parser.");
                        }

                        // Robust fallback parser in case Gemini returned string array for interviewQuestions or partial JSON
                        return ParseProfileAnalysisFallback(cleaned, profile, jobDescription);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse Gemini profile analysis output. Raw: {RawJson}", rawJson);
        }

        return AnalyzeProfileLocally(jobDescription, profile, "[Parsing Fallback]");
    }

    private ProfileAnalysisResult ParseProfileAnalysisFallback(string cleanedJson, DetailedCandidateProfileDto profile, JobDescriptionDto jobDescription)
    {
        try
        {
            using var doc = JsonDocument.Parse(cleanedJson);
            var root = doc.RootElement;

            string candidateId = root.TryGetProperty("candidateId", out var cidEl) && cidEl.GetString() is string cid && !string.IsNullOrWhiteSpace(cid) ? cid : profile.CandidateId;
            string candidateName = root.TryGetProperty("candidateName", out var cnameEl) && cnameEl.GetString() is string cname && !string.IsNullOrWhiteSpace(cname) ? cname : profile.FullName;
            int score = root.TryGetProperty("overallSuitabilityScore", out var sEl) && sEl.TryGetInt32(out var sc) ? sc : 50;
            string rec = root.TryGetProperty("recommendation", out var recEl) && recEl.GetString() is string r ? r : "BORDERLINE / INTERVIEW WITH CAUTION";
            string summary = root.TryGetProperty("executiveSummary", out var sumEl) && sumEl.GetString() is string sm ? sm : "Profile evaluated against requirements.";

            var techAudits = new List<TechUsageAudit>();
            if (root.TryGetProperty("techAudits", out var taEl) && taEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in taEl.EnumerateArray())
                {
                    string tech = item.TryGetProperty("technology", out var tEl) ? (tEl.GetString() ?? "") : "";
                    bool used = item.TryGetProperty("actuallyUsedInCareer", out var uEl) && uEl.GetBoolean();
                    string ev = item.TryGetProperty("careerEvidence", out var eEl) ? (eEl.GetString() ?? "") : "";
                    string depth = item.TryGetProperty("depthLevel", out var dEl) ? (dEl.GetString() ?? "") : "Secondary / Ancillary";
                    if (!string.IsNullOrWhiteSpace(tech))
                    {
                        techAudits.Add(new TechUsageAudit(tech, used, ev, depth));
                    }
                }
            }

            var strengths = new List<string>();
            if (root.TryGetProperty("strengths", out var strEl) && strEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in strEl.EnumerateArray())
                {
                    if (s.GetString() is string strVal && !string.IsNullOrWhiteSpace(strVal)) strengths.Add(strVal);
                }
            }

            var redFlags = new List<string>();
            if (root.TryGetProperty("redFlags", out var rfEl) && rfEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var rfItem in rfEl.EnumerateArray())
                {
                    if (rfItem.GetString() is string rfVal && !string.IsNullOrWhiteSpace(rfVal)) redFlags.Add(rfVal);
                }
            }

            var questions = new List<InterviewQuestionDto>();
            if (root.TryGetProperty("interviewQuestions", out var iqEl) && iqEl.ValueKind == JsonValueKind.Array)
            {
                int qIndex = 0;
                foreach (var q in iqEl.EnumerateArray())
                {
                    if (q.ValueKind == JsonValueKind.Object)
                    {
                        string qText = q.TryGetProperty("question", out var qt) ? (qt.GetString() ?? "") : "";
                        string diff = q.TryGetProperty("difficulty", out var qd) ? (qd.GetString() ?? "") : (qIndex < 2 ? "Simple" : "Advanced");
                        string ans = q.TryGetProperty("expectedAnswer", out var qa) ? (qa.GetString() ?? "") : "Candidate should explain practical hands-on experience and implementation.";
                        var keywords = new List<string>();
                        if (q.TryGetProperty("expectedKeywords", out var qk) && qk.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var kw in qk.EnumerateArray())
                            {
                                if (kw.GetString() is string kwStr && !string.IsNullOrWhiteSpace(kwStr)) keywords.Add(kwStr);
                            }
                        }
                        if (!string.IsNullOrWhiteSpace(qText))
                        {
                            questions.Add(new InterviewQuestionDto(qText, diff, ans, keywords));
                            qIndex++;
                        }
                    }
                    else if (q.ValueKind == JsonValueKind.String && q.GetString() is string strQ && !string.IsNullOrWhiteSpace(strQ))
                    {
                        questions.Add(new InterviewQuestionDto(
                            strQ,
                            qIndex < 2 ? "Simple" : "Advanced",
                            "Candidate should explain hands-on technical architecture and production decisions.",
                            new List<string> { "Practical Implementation", "Clean Architecture", "Production Reliability" }
                        ));
                        qIndex++;
                    }
                }
            }

            if (questions.Count == 0)
            {
                questions = GenerateDefaultInterviewQuestions(jobDescription.MandatorySkills);
            }

            return new ProfileAnalysisResult(
                candidateId,
                candidateName,
                score,
                rec,
                summary,
                techAudits,
                strengths,
                redFlags,
                questions
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ParseProfileAnalysisFallback manual parse failed. Reverting to local audit.");
            return AnalyzeProfileLocally(jobDescription, profile, "[Parsing Fallback]");
        }
    }

    private static ProfileAnalysisResult AnalyzeProfileLocally(JobDescriptionDto job, DetailedCandidateProfileDto profile, string prefix = "")
    {
        var mandatory = job.MandatorySkills ?? new List<string>();
        var experiences = profile.Experiences ?? new List<WorkExperienceDto>();
        var fullHistoryText = string.Join(" ", experiences.Select(e => $"{e.Company} {e.Designation} {e.Description}")).ToLowerInvariant();
        var rawText = (profile.FullProfileRawText ?? "").ToLowerInvariant();
        var allText = $"{fullHistoryText} {rawText}";

        var techAudits = new List<TechUsageAudit>();
        int authenticTechCount = 0;
        var strengths = new List<string>();
        var redFlags = new List<string>();

        // 1. Hands-on Experience & Skills Audit (TechUsageAudit)
        foreach (var skill in mandatory.Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            var skillLower = skill.Trim().ToLowerInvariant();
            bool inJobHistory = fullHistoryText.Contains(skillLower);
            bool inSkillsList = (profile.KeySkills ?? new List<string>()).Any(k => k.ToLowerInvariant().Contains(skillLower));

            if (inJobHistory)
            {
                authenticTechCount++;
                techAudits.Add(new TechUsageAudit(
                    skill.Trim(),
                    true,
                    $"Explicitly evidenced in hands-on work history across project descriptions.",
                    "Primary / Production"
                ));
            }
            else if (inSkillsList)
            {
                techAudits.Add(new TechUsageAudit(
                    skill.Trim(),
                    false,
                    "Listed in Key Skills section, but lacks explicit project descriptions in employment history.",
                    "Keyword Stuffed / Not Evident"
                ));
                redFlags.Add($"Skill '{skill.Trim()}' is listed in tags but not elaborated in project bullet points.");
            }
            else
            {
                techAudits.Add(new TechUsageAudit(
                    skill.Trim(),
                    false,
                    "Not found in project history or key skills.",
                    "Keyword Stuffed / Not Evident"
                ));
            }
        }

        // PURE 4-PILLAR SCORING BREAKDOWN (Strictly hands-on experience, skills, roles, companies):
        // Pillar 1: Hands-on Experience (0 - 35 pts)
        int handsOnScore = 0;
        if (mandatory.Count > 0)
        {
            handsOnScore = (int)Math.Round(((double)authenticTechCount / mandatory.Count) * 25);
        }
        else
        {
            handsOnScore = 20;
        }
        double years = ExtractYearsOfExperience(profile.TotalExperience);
        if (years >= 5) handsOnScore += 10;
        else if (years >= 3) handsOnScore += 7;
        else if (years >= 1) handsOnScore += 4;
        handsOnScore = Math.Min(35, handsOnScore);

        if (authenticTechCount > 0)
        {
            strengths.Add($"Verified hands-on production experience in {authenticTechCount} of {mandatory.Count} required core technologies.");
        }

        // Pillar 2: Skills Coverage & Breadth (0 - 30 pts)
        int skillsScore = 0;
        int totalKnownSkills = profile.KeySkills?.Count ?? 0;
        if (mandatory.Count > 0)
        {
            int matchedSkillsInTags = mandatory.Count(m => (profile.KeySkills ?? new List<string>()).Any(k => k.Contains(m.Trim(), StringComparison.OrdinalIgnoreCase)));
            skillsScore = (int)Math.Round(((double)matchedSkillsInTags / mandatory.Count) * 22);
            if (totalKnownSkills >= 5) skillsScore += 8;
            else skillsScore += Math.Min(8, totalKnownSkills * 2);
        }
        else
        {
            skillsScore = 25;
        }
        skillsScore = Math.Min(30, skillsScore);

        // Pillar 3: Role in which candidate has worked already (0 - 20 pts)
        int roleScore = 10;
        var jobTitleLower = (job.Title ?? "").ToLowerInvariant();
        var currentDesigLower = (profile.CurrentDesignation ?? "").ToLowerInvariant();

        bool hasSeniorOrLeadInRoles = experiences.Any(e => (e.Designation ?? "").Contains("lead", StringComparison.OrdinalIgnoreCase) ||
                                                           (e.Designation ?? "").Contains("senior", StringComparison.OrdinalIgnoreCase) ||
                                                           (e.Designation ?? "").Contains("architect", StringComparison.OrdinalIgnoreCase) ||
                                                           (e.Designation ?? "").Contains("principal", StringComparison.OrdinalIgnoreCase));

        bool roleMatchesJob = experiences.Any(e => (e.Designation ?? "").ToLowerInvariant().Contains("developer") ||
                                                   (e.Designation ?? "").ToLowerInvariant().Contains("engineer") ||
                                                   (jobTitleLower.Length > 0 && (e.Designation ?? "").ToLowerInvariant().Contains(jobTitleLower)));

        if (hasSeniorOrLeadInRoles && roleMatchesJob)
        {
            roleScore = 20;
            strengths.Add($"Strong career progression and proven seniority across roles: '{profile.CurrentDesignation}'.");
        }
        else if (roleMatchesJob)
        {
            roleScore = 16;
            strengths.Add($"Relevant functional roles in career history matching target position.");
        }
        else
        {
            roleScore = 10;
            redFlags.Add($"Previous roles show divergence from target role '{job.Title}'.");
        }

        // Pillar 4: Companies the candidate has worked (0 - 15 pts)
        int companiesScore = 8;
        int validCompanyCount = experiences.Count(e => !string.IsNullOrWhiteSpace(e.Company) && !e.Company.Equals("Company", StringComparison.OrdinalIgnoreCase));
        bool hasReputableCompanies = experiences.Any(e => {
            var comp = (e.Company ?? "").ToLowerInvariant();
            return comp.Contains("tech") || comp.Contains("solutions") || comp.Contains("systems") || comp.Contains("global") ||
                   comp.Contains("cloud") || comp.Contains("fintech") || comp.Contains("bank") || comp.Contains("ltd") || comp.Contains("pvt");
        });

        if (validCompanyCount >= 2 && hasReputableCompanies)
        {
            companiesScore = 15;
            strengths.Add($"Established engineering background across multiple technology organizations.");
        }
        else if (validCompanyCount >= 1)
        {
            companiesScore = hasReputableCompanies ? 13 : 10;
        }
        else
        {
            companiesScore = 8;
        }

        // Total Suitability Score is PURELY the sum of the 4 pillars (Max 100)
        int score = handsOnScore + skillsScore + roleScore + companiesScore;
        score = Math.Clamp(score, 15, 98);

        // Notice period is informational ONLY - does NOT affect the score
        string noticeDisplay = string.IsNullOrWhiteSpace(profile.NoticePeriod) || profile.NoticePeriod.Contains("not specified", StringComparison.OrdinalIgnoreCase) 
            ? "Not specified" 
            : profile.NoticePeriod;

        string recommendation = score >= 75 ? "WORTH GOING FORWARD"
            : score >= 50 ? "BORDERLINE / INTERVIEW WITH CAUTION"
            : "DO NOT PROCEED";

        var summary = $"Candidate has {profile.TotalExperience} of hands-on experience, having worked as '{profile.CurrentDesignation}' across {Math.Max(1, validCompanyCount)} company/companies. Audited {mandatory.Count} required technologies with {authenticTechCount} verified in direct project descriptions. Score is purely based on hands-on experience, skills, roles, and companies. [Notice period: {noticeDisplay} (Informational)].";
        if (!string.IsNullOrEmpty(prefix)) summary = $"{prefix} {summary}";

        var questions = GenerateDefaultInterviewQuestions(mandatory);

        return new ProfileAnalysisResult(
            profile.CandidateId,
            string.IsNullOrWhiteSpace(profile.FullName) ? "Candidate" : profile.FullName,
            score,
            recommendation,
            summary,
            techAudits,
            strengths,
            redFlags,
            questions
        );
    }

    private static List<InterviewQuestionDto> GenerateDefaultInterviewQuestions(List<string>? mandatorySkills)
    {
        var mandatory = mandatorySkills ?? new List<string>();
        var primaryTech = mandatory.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? ".NET Core / C#";
        var secondTech = mandatory.Skip(1).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "REST APIs";
        var dbTech = mandatory.FirstOrDefault(s => s.Contains("sql", StringComparison.OrdinalIgnoreCase) || s.Contains("data", StringComparison.OrdinalIgnoreCase) || s.Contains("mongo", StringComparison.OrdinalIgnoreCase) || s.Contains("postgres", StringComparison.OrdinalIgnoreCase)) ?? "Relational Database / SQL";

        return new List<InterviewQuestionDto>
        {
            new InterviewQuestionDto(
                $"Can you describe your day-to-day experience with {primaryTech} and how dependency injection / service lifetimes are managed in your code?",
                "Simple",
                $"The candidate should explain the core principles of {primaryTech} and describe how services are registered (Transient creates a new instance each time, Scoped creates one per request, Singleton creates one for the entire application).",
                new List<string> { "Dependency Injection", "Transient", "Scoped", "Singleton", "Constructor Injection" }
            ),
            new InterviewQuestionDto(
                $"How do you design, validate, and test secure RESTful APIs using {secondTech} in your projects?",
                "Simple",
                "The candidate should describe standard HTTP verbs (GET, POST, PUT, DELETE), proper HTTP status codes (200, 201, 400, 404, 500), model validation, and using tools like Postman, Swagger/OpenAPI, or automated tests.",
                new List<string> { "HTTP Verbs", "Status Codes (200, 400, 500)", "Model Validation", "Swagger / OpenAPI", "Unit Testing" }
            ),
            new InterviewQuestionDto(
                "In your work with microservices or distributed systems, how do you handle service-to-service communication, eventual consistency, and network failures?",
                "Advanced",
                "The candidate should explain whether they use synchronous REST/gRPC or asynchronous message brokers (RabbitMQ/Kafka). They should discuss handling distributed transactions with eventual consistency and resilience patterns like Circuit Breakers and retries.",
                new List<string> { "Message Broker (Kafka/RabbitMQ)", "Eventual Consistency", "Circuit Breaker", "gRPC / REST", "Resilience / Retries" }
            ),
            new InterviewQuestionDto(
                $"Walk me through how you diagnose and optimize database query bottlenecks using {dbTech}. How do you ensure high query performance?",
                "Advanced",
                $"The candidate should explain analyzing slow queries using execution plans, adding appropriate indexes (clustered/non-clustered), resolving N+1 query problems in ORMs, and utilizing caching (such as Redis) to reduce database load.",
                new List<string> { "Query Execution Plan", "Database Indexing", "N+1 Problem", "Connection Pooling", "Caching (Redis)" }
            ),
            new InterviewQuestionDto(
                "Describe a critical production bug, memory leak, or concurrency issue you investigated. How did you identify the root cause and ensure zero downtime?",
                "Advanced",
                "The candidate should walk through a real incident systematically: checking application logs and metrics, analyzing thread dumps or memory allocations, applying an immediate mitigation, and deploying a permanent fix with zero downtime.",
                new List<string> { "Root Cause Analysis", "Application Logs / APM", "Memory Leak / Deadlock", "Concurrency / Threading", "Zero-Downtime Deployment" }
            )
        };
    }

    private static int ExtractNoticePeriodDays(string? noticeText)
    {
        if (string.IsNullOrWhiteSpace(noticeText)) return -1;

        var lower = noticeText.ToLowerInvariant().Trim();
        if (lower.Contains("not specified") || lower.Contains("not disclosed") || lower.Contains("unknown") || lower.Contains("n/a"))
            return -1;

        if (lower.Contains("immediate") || lower.Contains("serving notice")) return 0;
        if (lower.Contains("15 days")) return 15;
        if (lower.Contains("1 month") || lower.Contains("30 days")) return 30;
        if (lower.Contains("2 month") || lower.Contains("60 days")) return 60;
        if (lower.Contains("3 month") || lower.Contains("90 days")) return 90;

        var match = Regex.Match(lower, @"(\d+)\s*(day|month|mth)");
        if (match.Success)
        {
            if (int.TryParse(match.Groups[1].Value, out int count))
            {
                if (match.Groups[2].Value.StartsWith("m"))
                {
                    return count * 30;
                }
                return count;
            }
        }

        return -1;
    }

    private static double ExtractYearsOfExperience(string? expText)
    {
        if (string.IsNullOrWhiteSpace(expText)) return 0;
        var match = Regex.Match(expText, @"(\d+(\.\d+)?)");
        if (match.Success && double.TryParse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out double years))
        {
            return years;
        }
        return 0;
    }
}

