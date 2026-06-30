"""DTOs for multi-source schema generation endpoint."""
from typing import Optional
from pydantic import BaseModel


class SourceFieldProfileDto(BaseModel):
    field_name: str
    inferred_type: str = "STRING"
    max_length: int = 0
    nullable: bool = True
    sample_values: list[str] = []


class SourceFileSchemaDto(BaseModel):
    file_name: str
    file_index: int
    fields: list[SourceFieldProfileDto]


class MultiSourceSchemaRequestDto(BaseModel):
    schema_name: str = "Generated Target Schema"
    source_files: list[SourceFileSchemaDto]


class SourceMappingDto(BaseModel):
    source_file_name: str
    source_file_index: int
    source_field: str
    merge_rule: str = "PRIMARY"  # PRIMARY | FALLBACK | CONCAT


class MultiSourceTargetFieldDto(BaseModel):
    target_field: str
    source_mappings: list[SourceMappingDto]
    datatype: str = "STRING"
    field_length: Optional[int] = None
    nullable: bool = True
    description: Optional[str] = None
    business_category: Optional[str] = None
    confidence: float = 0.80
    generation_method: str = "AI MULTI-SOURCE"


class MultiSourceSchemaResponseDto(BaseModel):
    schema_name: str
    fields: list[MultiSourceTargetFieldDto]
