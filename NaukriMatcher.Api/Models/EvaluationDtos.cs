namespace NaukriMatcher.Api.Models;

public record EvaluationRequest(
    JobDescriptionDto JobDescription,
    List<CandidateCardDto> Candidates
);

public record JobDescriptionDto(
    string Title,
    string RawText,
    List<string> MandatorySkills,
    int MaxNoticePeriodDays
);

public record CandidateCardDto(
    string CandidateId,
    string Designation,
    string TotalExperience,
    List<string> KeySkills,
    string NoticePeriod,
    string Location,
    string AnnualSalary,
    string Snippet,
    string? Company = ""
);

public record EvaluationResult(
    string CandidateId,
    int MatchPercentage,
    string Recommendation,
    string Reasoning
);

public record EvaluationResponse(
    List<EvaluationResult> Evaluations
);

// Detailed Full Profile & Resume Analysis Models
public record ProfileAnalysisRequest(
    JobDescriptionDto JobDescription,
    DetailedCandidateProfileDto Profile
);

public record DetailedCandidateProfileDto(
    string CandidateId,
    string FullName,
    string CurrentDesignation,
    string TotalExperience,
    string NoticePeriod,
    string CurrentLocation,
    string AnnualSalary,
    List<string> KeySkills,
    List<WorkExperienceDto> Experiences,
    string ResumeHeadline,
    string FullProfileRawText
);

public record WorkExperienceDto(
    string Company,
    string Designation,
    string Duration,
    string Description
);

public record ProfileAnalysisResult(
    string CandidateId,
    string CandidateName,
    int OverallSuitabilityScore,
    string Recommendation, // "WORTH GOING FORWARD", "BORDERLINE / INTERVIEW WITH CAUTION", "DO NOT PROCEED"
    string ExecutiveSummary,
    List<TechUsageAudit> TechAudits,
    List<string> Strengths,
    List<string> RedFlags,
    List<InterviewQuestionDto> InterviewQuestions
);

public record InterviewQuestionDto(
    string Question,
    string Difficulty, // "Simple" or "Advanced"
    string ExpectedAnswer,
    List<string> ExpectedKeywords
);

public record TechUsageAudit(
    string Technology,
    bool ActuallyUsedInCareer,
    string CareerEvidence,
    string DepthLevel // "Primary / Production", "Secondary / Ancillary", "Keyword Stuffed / Not Evident"
);
