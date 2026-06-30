"""
PromptLoader — Loads prompt templates from YAML files.
PromptManager — Renders prompts with variable substitution.
PromptVersioningService — Tracks prompt versions for audit.
"""
import os
from pathlib import Path
from typing import Dict, Optional

import yaml
from loguru import logger

from app.shared.constants.app_constants import LogCategories, WorkflowStepConstants
from app.shared.exceptions.base_exceptions import PromptException


_PROMPTS_DIR = Path(os.getenv("PROMPTS_PATH", "prompts"))


class PromptLoader:
    """
    Loads prompt templates from YAML files in the prompts directory.
    Caches loaded templates in memory.
    """

    def __init__(self, prompts_dir: Optional[Path] = None):
        self._prompts_dir = prompts_dir or _PROMPTS_DIR
        self._templates: Dict[str, dict] = {}
        logger.info(
            f"[PromptLoader] Initialized | prompts_dir={self._prompts_dir.resolve()}",
            category=LogCategories.AUDIT,
        )

    def load(self, prompt_id: str) -> dict:
        """
        Load a prompt template by ID.
        Prompt file must be named '{prompt_id}.yaml'.

        Args:
            prompt_id: Prompt identifier (e.g., 'semantic_mapping_v1').

        Returns:
            Parsed prompt template dictionary.

        Raises:
            PromptException: If prompt file not found or invalid.
        """
        if prompt_id in self._templates:
            logger.debug(
                f"[PromptLoader] Returning cached prompt | id={prompt_id}",
                category=LogCategories.AUDIT,
            )
            return self._templates[prompt_id]

        filepath = self._prompts_dir / f"{prompt_id}.yaml"
        if not filepath.exists():
            raise PromptException(
                f"Prompt template not found: {filepath}",
                prompt_id=prompt_id,
            )

        try:
            with open(filepath, "r", encoding="utf-8") as f:
                template = yaml.safe_load(f)
            self._templates[prompt_id] = template
            logger.info(
                f"[PromptLoader] Loaded prompt | id={prompt_id} | "
                f"version={template.get('version', 'unknown')}",
                category=LogCategories.AUDIT,
            )
            return template
        except yaml.YAMLError as exc:
            raise PromptException(
                f"Failed to parse prompt YAML '{prompt_id}': {str(exc)[:200]}",
                prompt_id=prompt_id,
            )

    def list_available(self) -> list:
        """List all available prompt IDs from the prompts directory."""
        if not self._prompts_dir.exists():
            return []
        return [p.stem for p in self._prompts_dir.glob("*.yaml")]


class PromptManager:
    """
    Renders prompt templates with variable substitution.
    Manages system_prompt and user_prompt rendering.
    """

    def __init__(self, loader: Optional[PromptLoader] = None):
        self._loader = loader or PromptLoader()
        logger.info("[PromptManager] Initialized", category=LogCategories.AUDIT)

    def render_system_prompt(self, prompt_id: str) -> str:
        """Render the system prompt for a given prompt ID."""
        template = self._loader.load(prompt_id)
        system_prompt = template.get("system_prompt", "")
        if not system_prompt:
            raise PromptException(
                f"Prompt '{prompt_id}' has no system_prompt defined.",
                prompt_id=prompt_id,
            )
        return system_prompt.strip()

    def render_user_prompt(self, prompt_id: str, **variables) -> str:
        """
        Render the user prompt template with provided variables.

        Args:
            prompt_id: Prompt identifier.
            **variables: Template variable substitutions.

        Returns:
            Rendered user prompt string.
        """
        template = self._loader.load(prompt_id)
        user_template = template.get("user_prompt_template", "")
        if not user_template:
            raise PromptException(
                f"Prompt '{prompt_id}' has no user_prompt_template defined.",
                prompt_id=prompt_id,
            )

        try:
            rendered = user_template.format(**variables)
            logger.debug(
                f"[PromptManager] Rendered user prompt | id={prompt_id} | "
                f"workflow_step={WorkflowStepConstants.PROMPT_RENDERING} | "
                f"rendered_length={len(rendered)}",
                category=LogCategories.AUDIT,
            )
            return rendered.strip()
        except KeyError as exc:
            raise PromptException(
                f"Prompt template '{prompt_id}' missing variable: {exc}",
                prompt_id=prompt_id,
            )

    def get_prompt_version(self, prompt_id: str) -> str:
        """Return the version string for a prompt."""
        template = self._loader.load(prompt_id)
        return template.get("version", "v1")


class PromptVersioningService:
    """
    Tracks prompt versions for audit and reproducibility.
    Ensures prompt versions are captured in AI audit records.
    """

    def __init__(self, manager: Optional[PromptManager] = None):
        self._manager = manager or PromptManager()

    def get_version_tag(self, prompt_id: str) -> str:
        """
        Return a version tag for audit tracking.

        Example: 'semantic_mapping_v3'
        """
        version = self._manager.get_prompt_version(prompt_id)
        return f"{prompt_id}_{version}"

    def audit_prompt_usage(
        self,
        prompt_id: str,
        job_id: str = "",
        request_id: str = "",
        workflow_step: str = "",
    ) -> str:
        """Log prompt version usage and return version tag."""
        version_tag = self.get_version_tag(prompt_id)
        logger.info(
            f"[PromptVersioningService] Prompt version audit | "
            f"prompt_id={prompt_id} | version_tag={version_tag} | "
            f"job_id={job_id} | request_id={request_id} | "
            f"workflow_step={workflow_step}",
            category=LogCategories.AUDIT,
        )
        return version_tag
