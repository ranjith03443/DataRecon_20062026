using DataReconciliation.Application.Transformations;
using DataReconciliation.Application.Transformations.Validation;
using DataReconciliation.Domain.Transformations;
using Microsoft.Extensions.DependencyInjection;

namespace DataReconciliation.Infrastructure.Transformations
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddDeterministicTransformationModule(this IServiceCollection services)
        {
            services.AddSingleton<ITransformationRegistryProvider, SupportedOperationsRegistryProvider>();
            services.AddScoped<TransformationRuleContractBuilder>();
            services.AddScoped<TransformationRuleValidator>();

            services.AddSingleton<ITransformationStrategy, DateFormatStrategy>();
            services.AddSingleton<ITransformationStrategy, TruncateStrategy>();
            services.AddSingleton<ITransformationStrategy, PaddingStrategy>();
            services.AddSingleton<ITransformationStrategy, DecimalFormatStrategy>();
            services.AddSingleton<ITransformationStrategy, ValueMappingStrategy>();
            services.AddSingleton<ITransformationStrategy, MaskingStrategy>();
            services.AddSingleton<ITransformationStrategy, ConcatStrategy>();
            services.AddSingleton<ITransformationStrategy, SplitStrategy>();
            services.AddSingleton<ITransformationStrategy, UppercaseStrategy>();
            services.AddSingleton<ITransformationStrategy, LowercaseStrategy>();
            services.AddSingleton<ITransformationStrategy, ProperCaseStrategy>();
            services.AddSingleton<ITransformationStrategy, BooleanMappingStrategy>();
            services.AddSingleton<ITransformationStrategy, CurrencyNormalizationStrategy>();

            services.AddSingleton<ITransformationStrategyFactory, TransformationStrategyFactory>();
            services.AddScoped<ITransformationExecutionService, TransformationExecutionService>();

            return services;
        }
    }
}
