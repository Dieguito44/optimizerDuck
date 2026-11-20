using optimizerDuck.Domain.Execution;
using optimizerDuck.Domain.Optimizations.Models;
using optimizerDuck.Domain.UI;
using optimizerDuck.Services.Optimization;

namespace optimizerDuck.Test.Services;

/// <summary>
///     Version 1 test (20/11/2026): the Bitácora said "Carpetas grandes fuera del indexador: falló"
///     with no reason, so nothing showed what had happened on the client's PC.
/// </summary>
public class ActivityLineTests
{
    [Fact]
    public void AFailure_SaysWhy()
    {
        var result = new OptimizationResult
        {
            Status = OptimizationSuccessResult.Failed,
            FailedSteps = [new Change { Name = "x", Error = "La búsqueda de Windows no está disponible." }],
        };

        var line = OperationRunner.ActivityLine(result, "Carpetas grandes", 0);

        Assert.Contains("Carpetas grandes", line);
        Assert.Contains("La búsqueda de Windows no está disponible", line);
    }

    [Fact]
    public void AFailureWithoutSteps_UsesTheResultMessage()
    {
        var result = new OptimizationResult
        {
            Status = OptimizationSuccessResult.Failed,
            Message = "Invalid RAM: 0",
        };

        Assert.Contains("Invalid RAM: 0", OperationRunner.ActivityLine(result, "Ajuste", 0));
    }

    [Fact]
    public void ChangesWithNoWayBack_AreCountedAndFlagged()
    {
        // Hyper-V test (20/11/2026): removing Edge said "listo, cambios: 0".
        var result = new OptimizationResult { Status = OptimizationSuccessResult.Success };

        var line = OperationRunner.ActivityLine(result, "Quitar Microsoft Edge", 0, 1);

        Assert.Contains("1", line);
        Assert.DoesNotContain("cambios: 0", line);
    }

    [Fact]
    public void ASuccess_StaysShort()
    {
        var result = new OptimizationResult
        {
            Status = OptimizationSuccessResult.Success,
            Message = "everything fine",
        };

        Assert.DoesNotContain("everything fine", OperationRunner.ActivityLine(result, "Ajuste", 1));
    }
}
