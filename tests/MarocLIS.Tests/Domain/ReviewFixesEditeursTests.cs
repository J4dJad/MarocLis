using System.Reflection;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Administration;
using MarocLIS.Application.Catalog;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Quality;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// Editors that wiped what they did not show (found while writing the user manual): « Modifier
/// l'analyse », « Modifier le paramètre », « Modifier un équipement » and « Modifier un
/// prescripteur » were filled from the grid row and saved blanks over every field the row does not
/// carry. They now open on the whole stored record (the services' Get…ForEditAsync) and save that
/// request with only the edited fields changed. Each test goes through that path: read the record
/// for editing, change one field as the dialog does, save, and check nothing else moved.
/// </summary>
public sealed class ReviewFixesEditeursTests
{
    // ---- Analysis ---------------------------------------------------------------------------------

    private static AnalysisTest FullAnalysis()
    {
        var analysis = new AnalysisTest("GLY", new MultilingualText("Glycémie", "سكر الدم", "Glucose"),
                                        Guid.NewGuid(), Guid.NewGuid())
        {
            ShortName = new MultilingualText("Gly", "سكر", "Glu"),
            SearchAliases = "glucose sucre",
            ContainerTypeId = Guid.NewGuid(),
            RequiredVolumeMl = 2.5m,
            LoincCode = "2345-7",
            NationalActCode = "0412",
            Method = new MultilingualText("Hexokinase", "هيكسوكيناز", "Hexokinase"),
            ReagentReference = "GLU-HK-500",
            IsAccredited = true,
            IsSubcontracted = true,
            SubcontractorSiteId = Guid.NewGuid(),
            RoutineTurnaroundHours = 24,
            UrgentTurnaroundMinutes = 45,
            IsAvailableUrgently = false,
            ScheduledDaysCsv = "1,3,5",
            FastingHoursOverride = 0,
            StabilityAtRoomTemperatureHours = 8,
            StabilityRefrigeratedHours = 72,
            RequiredClinicalInformation = new MultilingualText("Traitement en cours", "العلاج", "Treatment"),
            ConsentRequirement = ConsentRequirement.Genetic,
            IsNotifiableDisease = true,
            IsActive = true,
            DisplayOrder = 17
        };

        return analysis;
    }

    private static CatalogMaintenanceService CatalogService(AnalysisTest analysis, Action? onSave = null)
    {
        var analyses = Stub.Of<IRepository<AnalysisTest>>(new()
        {
            ["GetByIdAsync"] = _ => Task.FromResult<AnalysisTest?>(analysis),
            ["GetByIdWithDetailsAsync"] = _ => Task.FromResult<AnalysisTest?>(analysis)
        });

        var queries = Stub.Of<ICatalogMaintenanceQueries>(new()
        {
            ["GetUsedCodedValuesAsync"] = _ => Task.FromResult<IReadOnlyList<string>>([])
        });

        var unitOfWork = Stub.Of<IUnitOfWork>(new()
        {
            ["SaveChangesAsync"] = _ => { onSave?.Invoke(); return Task.FromResult(1); }
        });

        return new CatalogMaintenanceService(
            analyses, Stub.Of<IRepository<TestPanel>>(), Stub.Of<IRepository<ReflexRule>>(),
            Stub.Of<IRepository<Department>>(), Stub.Of<IRepository<SpecimenType>>(),
            Stub.Of<IRepository<ContainerType>>(), Stub.Of<IRepository<MeasurementUnit>>(),
            queries, unitOfWork, Stub.Of<IPermissionGuard>(), Stub.Of<IClock>());
    }

    [Fact]
    public async Task Editing_an_analysis_turnaround_keeps_every_other_field()
    {
        var analysis = FullAnalysis();
        var saves = 0;
        var service = CatalogService(analysis, () => saves++);

        var stored = await service.GetAnalysisForEditAsync(analysis.Id);
        stored.IsSuccess.Should().BeTrue();

        // What the dialog sends back when only the routine turnaround is changed.
        var edited = stored.Value with { RoutineTurnaroundHours = 48 };

        var saved = await service.SaveAnalysisAsync(edited);
        saved.IsSuccess.Should().BeTrue();
        saves.Should().Be(1);

        analysis.RoutineTurnaroundHours.Should().Be(48);
        analysis.LoincCode.Should().Be("2345-7");
        analysis.StabilityAtRoomTemperatureHours.Should().Be(8);
        analysis.StabilityRefrigeratedHours.Should().Be(72);
        analysis.FastingHoursOverride.Should().Be(0);
        analysis.RequiredClinicalInformation!.Ar.Should().Be("العلاج");
        analysis.IsNotifiableDisease.Should().BeTrue();
        analysis.UrgentTurnaroundMinutes.Should().Be(45);
        analysis.IsAvailableUrgently.Should().BeFalse();
        analysis.IsSubcontracted.Should().BeTrue();
        analysis.SubcontractorSiteId.Should().NotBeNull();
        analysis.ScheduledDaysCsv.Should().Be("1,3,5");
        analysis.DisplayOrder.Should().Be(17);

        // And field by field: reading it back gives exactly what was sent.
        var after = await service.GetAnalysisForEditAsync(analysis.Id);
        after.Value.Should().BeEquivalentTo(edited);
        after.Value.Should().BeEquivalentTo(stored.Value with { RoutineTurnaroundHours = 48 });
    }

    [Fact]
    public async Task Analysis_read_for_editing_carries_every_request_field()
    {
        var analysis = FullAnalysis();
        var stored = (await CatalogService(analysis).GetAnalysisForEditAsync(analysis.Id)).Value;

        // A property of the request left at its default here would be one the editor resets.
        AssertNoDefaults(stored, nameof(AnalysisRequest.IsAvailableUrgently),
                         nameof(AnalysisRequest.FastingHoursOverride));
    }

    // ---- Parameter ------------------------------------------------------------------------------

    [Fact]
    public async Task Editing_a_coded_parameter_keeps_its_answers_loinc_and_short_name()
    {
        var analysis = FullAnalysis();
        var parameter = analysis.AddParameter(
            new TestParameter("HBS", new MultilingualText("Antigène HBs"), ResultValueType.Coded)
            {
                ShortName = new MultilingualText("AgHBs", "مستضد", "HBsAg"),
                LoincCode = "5196-1",
                DisplayOrder = 3,
                IndentLevel = 1,
                IsInternalOnly = false,
                AlwaysShowOnReport = true
            });

        parameter.AddCodedOption(new CodedResultOption("NEG", new MultilingualText("Négatif", "سالب", "Negative"))
        {
            DisplayOrder = 1, IsExpectedNormal = true
        });
        parameter.AddCodedOption(new CodedResultOption("POS", new MultilingualText("Positif", "موجب", "Positive"))
        {
            DisplayOrder = 2, IsAbnormal = true, IsCritical = true
        });

        var service = CatalogService(analysis);

        var stored = await service.GetParameterForEditAsync(analysis.Id, parameter.Id);
        stored.IsSuccess.Should().BeTrue();
        stored.Value.CodedOptions.Select(o => o.Code).Should().Equal("NEG", "POS");
        stored.Value.LoincCode.Should().Be("5196-1");
        stored.Value.ShortName!.Fr.Should().Be("AgHBs");

        var edited = stored.Value with { Name = new MultilingualText("Antigène HBs (qualitatif)") };

        var saved = await service.SaveParameterAsync(edited);
        saved.IsSuccess.Should().BeTrue(string.Join(", ", saved.Errors.Select(e => e.ResourceKey)));

        parameter.Name.Fr.Should().Be("Antigène HBs (qualitatif)");
        parameter.LoincCode.Should().Be("5196-1");
        parameter.ShortName.Should().Be(new MultilingualText("AgHBs", "مستضد", "HBsAg"));
        parameter.CodedOptions.Should().HaveCount(2);

        var positive = parameter.CodedOptions.Single(o => o.Code == "POS");
        positive.Label.Should().Be(new MultilingualText("Positif", "موجب", "Positive"));
        positive.IsAbnormal.Should().BeTrue();
        positive.IsCritical.Should().BeTrue();
        parameter.CodedOptions.Single(o => o.Code == "NEG").IsExpectedNormal.Should().BeTrue();

        var after = await service.GetParameterForEditAsync(analysis.Id, parameter.Id);
        after.Value.Should().BeEquivalentTo(edited);
    }

    // ---- Instrument -----------------------------------------------------------------------------

    private static MasterDataService MasterService(Instrument? instrument = null,
                                                   Prescriber? prescriber = null)
    {
        var instruments = Stub.Of<IRepository<Instrument>>(new()
        {
            ["GetByIdAsync"] = _ => Task.FromResult(instrument)
        });

        var prescribers = Stub.Of<IRepository<Prescriber>>(new()
        {
            ["GetByIdAsync"] = _ => Task.FromResult(prescriber)
        });

        return new MasterDataService(
            instruments, Stub.Of<IRepository<QcMaterial>>(), Stub.Of<IRepository<QcTarget>>(),
            Stub.Of<IRepository<StockItem>>(), Stub.Of<IRepository<Supplier>>(), prescribers,
            Stub.Of<IRepository<Payer>>(), Stub.Of<IMasterDataQueries>(), Stub.Of<IUnitOfWork>(),
            Stub.Of<IPermissionGuard>(), Stub.Of<ICurrentUser>(), Stub.Of<IClock>());
    }

    [Fact]
    public async Task Editing_an_instrument_location_keeps_software_supplier_contract_and_dates()
    {
        var instrument = new Instrument("ARCH1", new MultilingualText("Architect", "أركيتكت", "Architect"),
                                        EquipmentKind.Analyser, Guid.NewGuid())
        {
            Manufacturer = "Abbott",
            Model = "ci4100",
            SerialNumber = "SN-0042",
            SoftwareVersion = "3.2.1",
            Location = "Biochimie",
            DepartmentId = Guid.NewGuid(),
            PurchaseDate = new DateOnly(2021, 3, 1),
            CommissioningDate = new DateOnly(2021, 4, 15),
            SupplierId = Guid.NewGuid(),
            MaintenanceContractReference = "CTR-2024-07",
            MaintenanceContractValidUntil = new DateOnly(2027, 6, 30),
            SupportPhone = "+212522000000",
            PreventiveMaintenanceIntervalDays = 90,
            LastPreventiveMaintenanceOn = new DateOnly(2026, 9, 1),
            CalibrationIntervalDays = 30,
            LastCalibrationOn = new DateOnly(2026, 10, 1),
            VerificationIntervalDays = 365,
            LastVerificationOn = new DateOnly(2026, 1, 10)
        };

        var service = MasterService(instrument);

        var stored = await service.GetInstrumentForEditAsync(instrument.Id);
        stored.IsSuccess.Should().BeTrue();
        AssertNoDefaults(stored.Value, nameof(InstrumentRequest.Kind));

        var edited = stored.Value with { Location = "Salle 2" };

        var saved = await service.SaveInstrumentAsync(edited);
        saved.IsSuccess.Should().BeTrue();

        instrument.Location.Should().Be("Salle 2");
        instrument.SoftwareVersion.Should().Be("3.2.1");
        instrument.SupplierId.Should().Be(stored.Value.SupplierId);
        instrument.MaintenanceContractReference.Should().Be("CTR-2024-07");
        instrument.SupportPhone.Should().Be("+212522000000");
        instrument.CommissioningDate.Should().Be(new DateOnly(2021, 4, 15));
        instrument.PurchaseDate.Should().Be(new DateOnly(2021, 3, 1));
        instrument.Name.En.Should().Be("Architect");

        (await service.GetInstrumentForEditAsync(instrument.Id)).Value.Should().BeEquivalentTo(edited);
    }

    // ---- Prescriber -----------------------------------------------------------------------------

    [Fact]
    public async Task Editing_a_prescriber_email_keeps_critical_phone_ice_fax_address_notes_and_payer()
    {
        var prescriber = new Prescriber("El Amrani", "Karim", PrescriberKind.Specialist)
        {
            FullNameArabic = "كريم العمراني",
            Speciality = new MultilingualText("Cardiologie", "أمراض القلب", "Cardiology"),
            ProfessionalOrderNumber = "ORD-1234",
            Inpe = "123456789",
            Ice = "002345678000012",
            Phone = "+212522112233",
            MobilePhone = "+212661223344",
            Email = "k.elamrani@example.ma",
            Fax = "+212522112234",
            AddressLine = "12, bd Zerktouni",
            City = "Casablanca",
            InstitutionName = "Clinique Atlas",
            DeliveryPreference = ReportDeliveryPreference.Email,
            SendPartialReports = true,
            CriticalResultPhone = "+212661998877",
            DiscountPercent = 10m,
            DefaultPayerId = Guid.NewGuid(),
            Notes = "Joindre le matin",
            IsActive = true
        };

        var service = MasterService(prescriber: prescriber);

        var stored = await service.GetPrescriberForEditAsync(prescriber.Id);
        stored.IsSuccess.Should().BeTrue();
        AssertNoDefaults(stored.Value);

        // Each telephone in its own field: the row's single number used to land in the mobile box.
        stored.Value.Phone.Should().Be("+212522112233");
        stored.Value.MobilePhone.Should().Be("+212661223344");
        stored.Value.LastName.Should().Be("El Amrani");

        var edited = stored.Value with { Email = "karim.elamrani@example.ma" };

        var saved = await service.SavePrescriberAsync(edited);
        saved.IsSuccess.Should().BeTrue(string.Join(", ", saved.Errors.Select(e => e.ResourceKey)));

        prescriber.Email.Should().Be("karim.elamrani@example.ma");
        prescriber.CriticalResultPhone.Should().Be("+212661998877");
        prescriber.Ice.Should().Be("002345678000012");
        prescriber.Fax.Should().Be("+212522112234");
        prescriber.AddressLine.Should().Be("12, bd Zerktouni");
        prescriber.Notes.Should().Be("Joindre le matin");
        prescriber.DefaultPayerId.Should().Be(stored.Value.DefaultPayerId);
        prescriber.Phone.Should().Be("+212522112233");
        prescriber.MobilePhone.Should().Be("+212661223344");
        prescriber.LastName.Should().Be("El Amrani");
        prescriber.FirstName.Should().Be("Karim");
        prescriber.Speciality!.En.Should().Be("Cardiology");

        (await service.GetPrescriberForEditAsync(prescriber.Id)).Value.Should().BeEquivalentTo(edited);
    }

    // ---- Helpers --------------------------------------------------------------------------------

    /// <summary>
    /// Fails on any property of a fully populated record that reads back as its type's default:
    /// a field the read-for-editing forgot, which the editor would then save as blank.
    /// </summary>
    private static void AssertNoDefaults(object request, params string[] legitimatelyDefault)
    {
        var defaults = request.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && p.Name != "EqualityContract")
            .Where(p => !legitimatelyDefault.Contains(p.Name))
            .Where(p =>
            {
                var value = p.GetValue(request);
                if (value is null) return true;
                var type = value.GetType();
                return type.IsValueType && value.Equals(Activator.CreateInstance(type));
            })
            .Select(p => p.Name)
            .ToList();

        defaults.Should().BeEmpty("every stored field must reach the editor");
    }

    private class Stub : DispatchProxy
    {
        private Dictionary<string, Func<object?[]?, object?>> _handlers = [];

        public static T Of<T>(Dictionary<string, Func<object?[]?, object?>>? handlers = null) where T : class
        {
            var proxy = Create<T, Stub>();
            ((Stub)(object)proxy)._handlers = handlers ?? [];
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null) return null;
            if (_handlers.TryGetValue(targetMethod.Name, out var handler)) return handler(args);
            return Neutral(targetMethod.ReturnType);
        }

        private static object? Neutral(Type type)
        {
            if (type == typeof(void)) return null;
            if (type == typeof(Result)) return Result.Success();
            if (type == typeof(Task)) return Task.CompletedTask;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var inner = type.GetGenericArguments()[0];
                var value = Neutral(inner);
                return typeof(Task).GetMethod(nameof(Task.FromResult))!
                                   .MakeGenericMethod(inner).Invoke(null, [value]);
            }
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }
    }
}
