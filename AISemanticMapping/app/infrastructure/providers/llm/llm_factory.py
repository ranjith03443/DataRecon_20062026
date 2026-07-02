"""
LLM Provider Factory — Configuration-driven provider instantiation.
Implements the Factory pattern for provider-agnostic LLM selection.
"""
from typing import Optional

from loguru import logger

from app.domain.enums.provider_enums import LLMProviderType
from app.infrastructure.config.config_loader import get_app_config
from app.infrastructure.providers.llm.illm_provider import ILLMProvider
from app.shared.constants.app_constants import LogCategories
from app.shared.exceptions.base_exceptions import ConfigurationException


class LLMProviderFactory:
    """
    Factory for creating LLM provider instances.
    Provider type is read from configuration — no hardcoding.
    """

    @staticmethod
    def create(
        provider_override: Optional[str] = None,
        task: Optional[str] = None,
    ) -> ILLMProvider:
        """
        Create and return an ILLMProvider implementation.

        Args:
            provider_override: Override provider name (optional).
            task: Task name for model configuration lookup (e.g., 'semantic_mapping').

        Returns:
            ILLMProvider implementation instance.

        Raises:
            ConfigurationException: If provider is unknown or not configured.
        """
        config = get_app_config()
        model_config = config.get_model_config(task or "semantic_mapping") if task else {}
        task_provider = model_config.get("provider") if model_config else None
        provider_name = (provider_override or task_provider or config.llm_provider).lower()

        # Auto-fallback: if a task requests azure_openai but no endpoint is configured
        # (e.g. running on a machine that only has Anthropic/Claude credentials),
        # transparently fall back to the global llm_provider so the service still works.
        if provider_name == LLMProviderType.AZURE_OPENAI and not config.azure_openai_endpoint:
            fallback = config.llm_provider.lower()
            logger.warning(
                f"[LLMProviderFactory] Azure OpenAI endpoint not configured — "
                f"falling back to '{fallback}' | task={task}",
                category=LogCategories.LLM_CALL,
            )
            provider_name = fallback
            model_config = None   # let the fallback provider use its own default config

        logger.info(
            f"[LLMProviderFactory] Creating provider | provider={provider_name} | task={task}",
            category=LogCategories.LLM_CALL,
        )

        provider: ILLMProvider

        if provider_name == LLMProviderType.AZURE_OPENAI:
            from app.infrastructure.providers.llm.azure_openai_provider import AzureOpenAIProvider
            provider = AzureOpenAIProvider(model_config=model_config or None)

        elif provider_name == LLMProviderType.OPENAI:
            from app.infrastructure.providers.llm.openai_provider import OpenAIProvider
            provider = OpenAIProvider(model_config=model_config or None)

        elif provider_name == LLMProviderType.OLLAMA:
            from app.infrastructure.providers.llm.ollama_provider import OllamaProvider
            provider = OllamaProvider(model_config=model_config or None)

        elif provider_name == LLMProviderType.CLAUDE:
            from app.infrastructure.providers.llm.claude_provider import ClaudeProvider
            provider = ClaudeProvider(model_config=model_config or None)

        elif provider_name == LLMProviderType.GEMINI:
            from app.infrastructure.providers.llm.gemini_provider import GeminiProvider
            provider = GeminiProvider(model_config=model_config or None)

        elif provider_name == LLMProviderType.MISTRAL:
            from app.infrastructure.providers.llm.mistral_provider import MistralProvider
            provider = MistralProvider(model_config=model_config or None)

        elif provider_name == LLMProviderType.HUGGINGFACE:
            raise ConfigurationException(
                f"HuggingFace provider not yet implemented. Set llm_provider to 'azure_openai'.",
                config_key="llm_provider",
            )

        else:
            raise ConfigurationException(
                f"Unknown LLM provider: '{provider_name}'. "
                f"Supported: {[e.value for e in LLMProviderType]}",
                config_key="llm_provider",
            )

        from app.infrastructure.providers.llm.budget_tracking_provider import BudgetTrackingProvider
        return BudgetTrackingProvider(provider, task=task or "unknown")
