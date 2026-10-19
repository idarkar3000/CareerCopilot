namespace CareerCopilot.Endpoints
{
    public static class HealthEndpoints
    {
        // GET y HEAD porque UptimeRobot y Render usan ambos para mantener el servicio despierto
        public static void MapHealthEndpoints(this WebApplication app)
        {
            app.MapMethods("/", ["GET", "HEAD"], () => Results.Ok(new
            {
                status = "Healthy",
                service = "CareerCopilot",
                timestamp = DateTime.UtcNow
            }));

            app.MapMethods("/health", ["GET", "HEAD"], () => Results.Ok("OK"));
        }
    }
}
