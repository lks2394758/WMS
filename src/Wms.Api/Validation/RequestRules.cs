using Wms.Api.Errors;
using Wms.Application.Inventory;
using Wms.Contracts.Auth;
using Wms.Contracts.Common;
using Wms.Contracts.Inventory;
using Wms.Domain.Inventory;

namespace Wms.Api.Validation;

/// <summary>
/// Field-level checks of the command requests at the API boundary, so a client sees every invalid field at once,
/// keyed by its JSON path. They mirror the use-case and domain rules, which still run and stay authoritative;
/// checks that need stored data (existence, active flags, stock) are left to the use case.
/// </summary>
internal static class RequestRules
{
    public static FieldErrors IdempotencyKey(string? value)
    {
        var errors = new FieldErrors();
        if (!Guid.TryParse(value, out var key) || key == Guid.Empty)
        {
            errors.Add(ApiHeaders.IdempotencyKey, "Must be a non-empty UUID, generated once per user action.");
        }

        return errors;
    }

    public static FieldErrors Validate(ReceiptRequest? request) =>
        Body(request, (errors, r) =>
        {
            RequiredId(errors, "locationId", r.LocationId);
            QuantityLines(errors, r.Lines);
            ReferenceNo(errors, r.ReferenceNo);
        });

    public static FieldErrors Validate(MovementRequest? request) =>
        Body(request, (errors, r) =>
        {
            RequiredId(errors, "fromLocationId", r.FromLocationId);
            RequiredId(errors, "toLocationId", r.ToLocationId);
            if (r.FromLocationId != Guid.Empty && r.FromLocationId == r.ToLocationId)
            {
                errors.Add("toLocationId", "Must differ from fromLocationId.");
            }

            QuantityLines(errors, r.Lines);
            ReferenceNo(errors, r.ReferenceNo);
        });

    public static FieldErrors Validate(ShipmentRequest? request) =>
        Body(request, (errors, r) =>
        {
            RequiredId(errors, "locationId", r.LocationId);
            QuantityLines(errors, r.Lines);
            ReferenceNo(errors, r.ReferenceNo);
        });

    public static FieldErrors Validate(CountRequest? request) =>
        Body(request, (errors, r) =>
        {
            RequiredId(errors, "locationId", r.LocationId);
            Reason(errors, r.Reason);
            Lines(errors, r.Lines, l => l.ProductId, (field, l) =>
            {
                if (l.CountedQuantity < 0)
                {
                    errors.Add(field + ".countedQuantity", "Must be zero or greater.");
                }
            });
        });

    public static FieldErrors Validate(AdjustmentRequest? request) =>
        Body(request, (errors, r) =>
        {
            RequiredId(errors, "locationId", r.LocationId);
            Reason(errors, r.Reason);
            Lines(errors, r.Lines, l => l.ProductId, (field, l) =>
            {
                if (l.QuantityDelta == 0)
                {
                    errors.Add(field + ".quantityDelta", "Must be a non-zero whole number.");
                }
            });
            ReferenceNo(errors, r.ReferenceNo);
        });

    public static FieldErrors Validate(LoginRequest? request) =>
        Body(request, (errors, r) =>
        {
            if (string.IsNullOrWhiteSpace(r.Username))
            {
                errors.Add("username", "Is required.");
            }

            if (string.IsNullOrEmpty(r.Password))
            {
                errors.Add("password", "Is required.");
            }
        });

    public static FieldErrors Validate(RefreshRequest? request) =>
        Body(request, (errors, r) =>
        {
            if (string.IsNullOrWhiteSpace(r.RefreshToken))
            {
                errors.Add("refreshToken", "Is required.");
            }
        });

    private static FieldErrors Body<T>(T? request, Action<FieldErrors, T> rules)
        where T : class
    {
        var errors = new FieldErrors();
        if (request is null)
        {
            errors.Add(ValidationProblems.BodyField, "A JSON request body is required.");
        }
        else
        {
            rules(errors, request);
        }

        return errors;
    }

    private static void RequiredId(FieldErrors errors, string field, Guid value)
    {
        if (value == Guid.Empty)
        {
            errors.Add(field, "Is required and must be a non-empty UUID.");
        }
    }

    private static void QuantityLines(FieldErrors errors, IReadOnlyList<QuantityLine>? lines) =>
        Lines(errors, lines, l => l.ProductId, (field, l) =>
        {
            if (l.Quantity <= 0)
            {
                errors.Add(field + ".quantity", "Must be greater than zero.");
            }
        });

    private static void Lines<TLine>(
        FieldErrors errors, IReadOnlyList<TLine>? lines, Func<TLine, Guid> productId, Action<string, TLine> lineRules)
        where TLine : class
    {
        if (lines is null || lines.Count == 0)
        {
            errors.Add("lines", "Must contain at least one line.");
            return;
        }

        if (lines.Count > InventoryCommandService.MaxLines)
        {
            errors.Add("lines", $"Must contain at most {InventoryCommandService.MaxLines} lines.");
            return;
        }

        var seen = new HashSet<Guid>();
        for (var i = 0; i < lines.Count; i++)
        {
            var field = $"lines[{i}]";
            var line = lines[i];
            if (line is null)
            {
                errors.Add(field, "Is required.");
                continue;
            }

            var id = productId(line);
            if (id == Guid.Empty)
            {
                errors.Add(field + ".productId", "Is required and must be a non-empty UUID.");
            }
            else if (!seen.Add(id))
            {
                errors.Add(field + ".productId", "Each product can appear only once per request.");
            }

            lineRules(field, line);
        }
    }

    private static void Reason(FieldErrors errors, string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            errors.Add("reason", "Is required.");
        }
        else if (reason.Length > InventoryTransaction.MaxReasonLength)
        {
            errors.Add("reason", $"Must be at most {InventoryTransaction.MaxReasonLength} characters.");
        }
    }

    private static void ReferenceNo(FieldErrors errors, string? referenceNo)
    {
        if (referenceNo is { Length: > InventoryTransaction.MaxReferenceLength })
        {
            errors.Add("referenceNo", $"Must be at most {InventoryTransaction.MaxReferenceLength} characters.");
        }
    }
}

/// <summary>Request field (JSON path or header name) to messages, in the order found.</summary>
internal sealed class FieldErrors
{
    private readonly Dictionary<string, List<string>> _errors = new(StringComparer.Ordinal);

    public bool IsEmpty => _errors.Count == 0;

    public void Add(string field, string message)
    {
        if (!_errors.TryGetValue(field, out var messages))
        {
            _errors[field] = messages = [];
        }

        messages.Add(message);
    }

    public FieldErrors Merge(FieldErrors other)
    {
        ArgumentNullException.ThrowIfNull(other);
        foreach (var (field, messages) in other._errors)
        {
            messages.ForEach(m => Add(field, m));
        }

        return this;
    }

    public IReadOnlyDictionary<string, string[]> ToDictionary() =>
        _errors.ToDictionary(e => e.Key, e => e.Value.ToArray(), StringComparer.Ordinal);
}
