namespace CareerCopilot.Models;

public record JobOffer(
    string Id,
    string Title,
    string Company,
    string Link,
    string Description,
    DateTime PublishedDate
);