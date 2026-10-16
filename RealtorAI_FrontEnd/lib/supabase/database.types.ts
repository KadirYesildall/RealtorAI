// Generated via `supabase gen types typescript --local` (run from RealtorAI_BackEnd/) against
// the running local stack — this is the source of truth; regenerate after schema changes
// rather than hand-editing.

export type Json = string | number | boolean | null | { [key: string]: Json | undefined } | Json[]

export type Database = {
  
  "public": {
          Tables: {
            "ai_model_registry": {
                  Row: {
                    "enabled": boolean,"endpoint": string | null,"eval_result": Json | null,"eval_status": Database["public"]['Enums']["model_eval_status"],"fallback_registry_id": string | null,"id": string,"license": string | null,"model_id": string,"parameters": NonNullable<Json>,"provider": string,"task_key": string,"verified_at": string | null
                  }
                  ComputedFields: never
                  Insert: {
                    "enabled"?: boolean,"endpoint"?: string | null,"eval_result"?: Json | null,"eval_status"?: Database["public"]['Enums']["model_eval_status"],"fallback_registry_id"?: string | null,"id"?: string,"license"?: string | null,"model_id": string,"parameters"?: NonNullable<Json>,"provider": string,"task_key": string,"verified_at"?: string | null
                  }
                  Update: {
                    "enabled"?: boolean,"endpoint"?: string | null,"eval_result"?: Json | null,"eval_status"?: Database["public"]['Enums']["model_eval_status"],"fallback_registry_id"?: string | null,"id"?: string,"license"?: string | null,"model_id"?: string,"parameters"?: NonNullable<Json>,"provider"?: string,"task_key"?: string,"verified_at"?: string | null
                  }
                  Relationships: [
                    {
      foreignKeyName: "ai_model_registry_fallback_registry_id_fkey"
      columns: ["fallback_registry_id"]
isOneToOne: false
      referencedRelation: "ai_model_registry"
      referencedColumns: ["id"]
    }
                  ]
                },"ai_usage": {
                  Row: {
                    "cache_hit": boolean,"cost_usd": number | null,"created_at": string,"id": string,"image_count": number,"listing_id": string,"model_id": string,"task_key": string,"tokens_in": number,"tokens_out": number
                  }
                  ComputedFields: never
                  Insert: {
                    "cache_hit"?: boolean,"cost_usd"?: number | null,"created_at"?: string,"id"?: string,"image_count"?: number,"listing_id": string,"model_id": string,"task_key": string,"tokens_in"?: number,"tokens_out"?: number
                  }
                  Update: {
                    "cache_hit"?: boolean,"cost_usd"?: number | null,"created_at"?: string,"id"?: string,"image_count"?: number,"listing_id"?: string,"model_id"?: string,"task_key"?: string,"tokens_in"?: number,"tokens_out"?: number
                  }
                  Relationships: [
                    {
      foreignKeyName: "ai_usage_listing_id_fkey"
      columns: ["listing_id"]
isOneToOne: false
      referencedRelation: "listings"
      referencedColumns: ["id"]
    }
                  ]
                },"approvals": {
                  Row: {
                    "approved_at": string,"approved_by": string,"channel": string,"content_hash": string,"id": string,"listing_id": string,"revoked_at": string | null,"revoked_reason": string | null,"target_id": string,"target_type": string
                  }
                  ComputedFields: never
                  Insert: {
                    "approved_at"?: string,"approved_by": string,"channel": string,"content_hash": string,"id"?: string,"listing_id": string,"revoked_at"?: string | null,"revoked_reason"?: string | null,"target_id": string,"target_type": string
                  }
                  Update: {
                    "approved_at"?: string,"approved_by"?: string,"channel"?: string,"content_hash"?: string,"id"?: string,"listing_id"?: string,"revoked_at"?: string | null,"revoked_reason"?: string | null,"target_id"?: string,"target_type"?: string
                  }
                  Relationships: [
                    {
      foreignKeyName: "approvals_approved_by_fkey"
      columns: ["approved_by"]
isOneToOne: false
      referencedRelation: "profiles"
      referencedColumns: ["id"]
    },{
      foreignKeyName: "approvals_listing_id_fkey"
      columns: ["listing_id"]
isOneToOne: false
      referencedRelation: "listings"
      referencedColumns: ["id"]
    }
                  ]
                },"audit_events": {
                  Row: {
                    "action": Database["public"]['Enums']["audit_action"],"actor_id": string | null,"after_value": Json | null,"before_value": Json | null,"brokerage_id": string | null,"created_at": string,"id": string,"input_ref": string | null,"listing_id": string | null,"model_id": string | null,"output_ref": string | null,"payload_hash": string | null,"prompt_hash": string | null,"prompt_version": string | null,"target_id": string | null,"target_type": string | null
                  }
                  ComputedFields: never
                  Insert: {
                    "action": Database["public"]['Enums']["audit_action"],"actor_id"?: string | null,"after_value"?: Json | null,"before_value"?: Json | null,"brokerage_id"?: string | null,"created_at"?: string,"id"?: string,"input_ref"?: string | null,"listing_id"?: string | null,"model_id"?: string | null,"output_ref"?: string | null,"payload_hash"?: string | null,"prompt_hash"?: string | null,"prompt_version"?: string | null,"target_id"?: string | null,"target_type"?: string | null
                  }
                  Update: {
                    "action"?: Database["public"]['Enums']["audit_action"],"actor_id"?: string | null,"after_value"?: Json | null,"before_value"?: Json | null,"brokerage_id"?: string | null,"created_at"?: string,"id"?: string,"input_ref"?: string | null,"listing_id"?: string | null,"model_id"?: string | null,"output_ref"?: string | null,"payload_hash"?: string | null,"prompt_hash"?: string | null,"prompt_version"?: string | null,"target_id"?: string | null,"target_type"?: string | null
                  }
                  Relationships: [
                    {
      foreignKeyName: "audit_events_actor_id_fkey"
      columns: ["actor_id"]
isOneToOne: false
      referencedRelation: "profiles"
      referencedColumns: ["id"]
    },{
      foreignKeyName: "audit_events_brokerage_id_fkey"
      columns: ["brokerage_id"]
isOneToOne: false
      referencedRelation: "brokerages"
      referencedColumns: ["id"]
    },{
      foreignKeyName: "audit_events_listing_id_fkey"
      columns: ["listing_id"]
isOneToOne: false
      referencedRelation: "listings"
      referencedColumns: ["id"]
    }
                  ]
                },"brokerage_memberships": {
                  Row: {
                    "agent_id": string,"brokerage_id": string,"decided_at": string | null,"decided_by": string | null,"id": string,"requested_at": string,"role": Database["public"]['Enums']["membership_role"],"status": Database["public"]['Enums']["membership_status"]
                  }
                  ComputedFields: never
                  Insert: {
                    "agent_id": string,"brokerage_id": string,"decided_at"?: string | null,"decided_by"?: string | null,"id"?: string,"requested_at"?: string,"role"?: Database["public"]['Enums']["membership_role"],"status"?: Database["public"]['Enums']["membership_status"]
                  }
                  Update: {
                    "agent_id"?: string,"brokerage_id"?: string,"decided_at"?: string | null,"decided_by"?: string | null,"id"?: string,"requested_at"?: string,"role"?: Database["public"]['Enums']["membership_role"],"status"?: Database["public"]['Enums']["membership_status"]
                  }
                  Relationships: [
                    {
      foreignKeyName: "brokerage_memberships_agent_id_fkey"
      columns: ["agent_id"]
isOneToOne: false
      referencedRelation: "profiles"
      referencedColumns: ["id"]
    },{
      foreignKeyName: "brokerage_memberships_brokerage_id_fkey"
      columns: ["brokerage_id"]
isOneToOne: false
      referencedRelation: "brokerages"
      referencedColumns: ["id"]
    },{
      foreignKeyName: "brokerage_memberships_decided_by_fkey"
      columns: ["decided_by"]
isOneToOne: false
      referencedRelation: "profiles"
      referencedColumns: ["id"]
    }
                  ]
                },"brokerage_settings": {
                  Row: {
                    "ai_budget_images": number,"ai_budget_tokens": number,"brokerage_id": string,"default_tone": Database["public"]['Enums']["tone"],"target_mls_id": string | null
                  }
                  ComputedFields: never
                  Insert: {
                    "ai_budget_images"?: number,"ai_budget_tokens"?: number,"brokerage_id": string,"default_tone"?: Database["public"]['Enums']["tone"],"target_mls_id"?: string | null
                  }
                  Update: {
                    "ai_budget_images"?: number,"ai_budget_tokens"?: number,"brokerage_id"?: string,"default_tone"?: Database["public"]['Enums']["tone"],"target_mls_id"?: string | null
                  }
                  Relationships: [
                    {
      foreignKeyName: "brokerage_settings_brokerage_id_fkey"
      columns: ["brokerage_id"]
isOneToOne: true
      referencedRelation: "brokerages"
      referencedColumns: ["id"]
    }
                  ]
                },"brokerages": {
                  Row: {
                    "business_calendar": NonNullable<Json>,"created_at": string,"id": string,"license_number": string,"logo_storage_key": string | null,"name": string,"required_disclaimers": NonNullable<Json>,"state": string
                  }
                  ComputedFields: never
                  Insert: {
                    "business_calendar"?: NonNullable<Json>,"created_at"?: string,"id"?: string,"license_number": string,"logo_storage_key"?: string | null,"name": string,"required_disclaimers"?: NonNullable<Json>,"state": string
                  }
                  Update: {
                    "business_calendar"?: NonNullable<Json>,"created_at"?: string,"id"?: string,"license_number"?: string,"logo_storage_key"?: string | null,"name"?: string,"required_disclaimers"?: NonNullable<Json>,"state"?: string
                  }
                  Relationships: [
                    
                  ]
                },"compliance_findings": {
                  Row: {
                    "acknowledged_at": string | null,"acknowledged_by": string | null,"acknowledged_reason": string | null,"check_type": Database["public"]['Enums']["check_type"],"created_at": string,"id": string,"lexicon_version": string | null,"listing_id": string,"model_id": string | null,"phrase": string | null,"prompt_version": string | null,"reason": string,"severity": Database["public"]['Enums']["finding_severity"],"source": Database["public"]['Enums']["finding_source"],"span_end": number | null,"span_start": number | null,"status": Database["public"]['Enums']["finding_status"],"suggested_fix": string | null,"target_id": string,"target_type": string
                  }
                  ComputedFields: never
                  Insert: {
                    "acknowledged_at"?: string | null,"acknowledged_by"?: string | null,"acknowledged_reason"?: string | null,"check_type": Database["public"]['Enums']["check_type"],"created_at"?: string,"id"?: string,"lexicon_version"?: string | null,"listing_id": string,"model_id"?: string | null,"phrase"?: string | null,"prompt_version"?: string | null,"reason": string,"severity": Database["public"]['Enums']["finding_severity"],"source": Database["public"]['Enums']["finding_source"],"span_end"?: number | null,"span_start"?: number | null,"status"?: Database["public"]['Enums']["finding_status"],"suggested_fix"?: string | null,"target_id": string,"target_type": string
                  }
                  Update: {
                    "acknowledged_at"?: string | null,"acknowledged_by"?: string | null,"acknowledged_reason"?: string | null,"check_type"?: Database["public"]['Enums']["check_type"],"created_at"?: string,"id"?: string,"lexicon_version"?: string | null,"listing_id"?: string,"model_id"?: string | null,"phrase"?: string | null,"prompt_version"?: string | null,"reason"?: string,"severity"?: Database["public"]['Enums']["finding_severity"],"source"?: Database["public"]['Enums']["finding_source"],"span_end"?: number | null,"span_start"?: number | null,"status"?: Database["public"]['Enums']["finding_status"],"suggested_fix"?: string | null,"target_id"?: string,"target_type"?: string
                  }
                  Relationships: [
                    {
      foreignKeyName: "compliance_findings_acknowledged_by_fkey"
      columns: ["acknowledged_by"]
isOneToOne: false
      referencedRelation: "profiles"
      referencedColumns: ["id"]
    },{
      foreignKeyName: "compliance_findings_listing_id_fkey"
      columns: ["listing_id"]
isOneToOne: false
      referencedRelation: "listings"
      referencedColumns: ["id"]
    }
                  ]
                },"content_drafts": {
                  Row: {
                    "body": string,"created_at": string,"id": string,"kind": Database["public"]['Enums']["content_kind"],"listing_id": string,"model_id": string | null,"prompt_hash": string | null,"prompt_version": string | null,"row_version": number,"selected": boolean,"status": Database["public"]['Enums']["content_status"],"supporting_field_ids": NonNullable<Json>,"tone": Database["public"]['Enums']["tone"] | null,"variant_index": number,"version": number
                  }
                  ComputedFields: never
                  Insert: {
                    "body": string,"created_at"?: string,"id"?: string,"kind": Database["public"]['Enums']["content_kind"],"listing_id": string,"model_id"?: string | null,"prompt_hash"?: string | null,"prompt_version"?: string | null,"row_version"?: number,"selected"?: boolean,"status"?: Database["public"]['Enums']["content_status"],"supporting_field_ids"?: NonNullable<Json>,"tone"?: Database["public"]['Enums']["tone"] | null,"variant_index"?: number,"version"?: number
                  }
                  Update: {
                    "body"?: string,"created_at"?: string,"id"?: string,"kind"?: Database["public"]['Enums']["content_kind"],"listing_id"?: string,"model_id"?: string | null,"prompt_hash"?: string | null,"prompt_version"?: string | null,"row_version"?: number,"selected"?: boolean,"status"?: Database["public"]['Enums']["content_status"],"supporting_field_ids"?: NonNullable<Json>,"tone"?: Database["public"]['Enums']["tone"] | null,"variant_index"?: number,"version"?: number
                  }
                  Relationships: [
                    {
      foreignKeyName: "content_drafts_listing_id_fkey"
      columns: ["listing_id"]
isOneToOne: false
      referencedRelation: "listings"
      referencedColumns: ["id"]
    }
                  ]
                },"listing_fields": {
                  Row: {
                    "confirmed": boolean,"confirmed_at": string | null,"confirmed_by": string | null,"id": string,"listing_id": string,"provenance": Database["public"]['Enums']["provenance"],"reso_field_key": string,"row_version": number,"source_note": string | null,"value": NonNullable<Json>
                  }
                  ComputedFields: never
                  Insert: {
                    "confirmed"?: boolean,"confirmed_at"?: string | null,"confirmed_by"?: string | null,"id"?: string,"listing_id": string,"provenance": Database["public"]['Enums']["provenance"],"reso_field_key": string,"row_version"?: number,"source_note"?: string | null,"value": NonNullable<Json>
                  }
                  Update: {
                    "confirmed"?: boolean,"confirmed_at"?: string | null,"confirmed_by"?: string | null,"id"?: string,"listing_id"?: string,"provenance"?: Database["public"]['Enums']["provenance"],"reso_field_key"?: string,"row_version"?: number,"source_note"?: string | null,"value"?: NonNullable<Json>
                  }
                  Relationships: [
                    {
      foreignKeyName: "listing_fields_confirmed_by_fkey"
      columns: ["confirmed_by"]
isOneToOne: false
      referencedRelation: "profiles"
      referencedColumns: ["id"]
    },{
      foreignKeyName: "listing_fields_listing_id_fkey"
      columns: ["listing_id"]
isOneToOne: false
      referencedRelation: "listings"
      referencedColumns: ["id"]
    }
                  ]
                },"listings": {
                  Row: {
                    "address_line1": string,"agent_id": string,"agent_notes": string | null,"bathrooms": number | null,"bedrooms": number | null,"brokerage_id": string | null,"city": string,"cover_photo_id": string | null,"created_at": string,"deleted_at": string | null,"id": string,"list_price": number | null,"postal_code": string,"property_type": string,"row_version": number,"state": string,"status": Database["public"]['Enums']["listing_status"],"target_mls_id": string | null,"updated_at": string
                  }
                  ComputedFields: never
                  Insert: {
                    "address_line1": string,"agent_id": string,"agent_notes"?: string | null,"bathrooms"?: number | null,"bedrooms"?: number | null,"brokerage_id"?: string | null,"city": string,"cover_photo_id"?: string | null,"created_at"?: string,"deleted_at"?: string | null,"id"?: string,"list_price"?: number | null,"postal_code": string,"property_type": string,"row_version"?: number,"state": string,"status"?: Database["public"]['Enums']["listing_status"],"target_mls_id"?: string | null,"updated_at"?: string
                  }
                  Update: {
                    "address_line1"?: string,"agent_id"?: string,"agent_notes"?: string | null,"bathrooms"?: number | null,"bedrooms"?: number | null,"brokerage_id"?: string | null,"city"?: string,"cover_photo_id"?: string | null,"created_at"?: string,"deleted_at"?: string | null,"id"?: string,"list_price"?: number | null,"postal_code"?: string,"property_type"?: string,"row_version"?: number,"state"?: string,"status"?: Database["public"]['Enums']["listing_status"],"target_mls_id"?: string | null,"updated_at"?: string
                  }
                  Relationships: [
                    {
      foreignKeyName: "listings_agent_id_fkey"
      columns: ["agent_id"]
isOneToOne: false
      referencedRelation: "profiles"
      referencedColumns: ["id"]
    },{
      foreignKeyName: "listings_brokerage_id_fkey"
      columns: ["brokerage_id"]
isOneToOne: false
      referencedRelation: "brokerages"
      referencedColumns: ["id"]
    },{
      foreignKeyName: "listings_cover_photo_fk"
      columns: ["cover_photo_id"]
isOneToOne: false
      referencedRelation: "photos"
      referencedColumns: ["id"]
    }
                  ]
                },"mls_rule_profiles": {
                  Row: {
                    "allowed_alteration_types": NonNullable<Json>,"disclosure_labels": NonNullable<Json>,"display_name": string,"enumerated_field_lookups": NonNullable<Json>,"forbidden_in_public_remarks": NonNullable<Json>,"id": string,"photo_max_count": number | null,"photo_min_count": number | null,"photo_min_height": number | null,"photo_min_width": number | null,"private_remarks_max_chars": number | null,"public_remarks_max_chars": number | null,"required_fields": NonNullable<Json>,"rulebook_version": string | null,"verified_at": string | null
                  }
                  ComputedFields: never
                  Insert: {
                    "allowed_alteration_types"?: NonNullable<Json>,"disclosure_labels"?: NonNullable<Json>,"display_name": string,"enumerated_field_lookups"?: NonNullable<Json>,"forbidden_in_public_remarks"?: NonNullable<Json>,"id": string,"photo_max_count"?: number | null,"photo_min_count"?: number | null,"photo_min_height"?: number | null,"photo_min_width"?: number | null,"private_remarks_max_chars"?: number | null,"public_remarks_max_chars"?: number | null,"required_fields"?: NonNullable<Json>,"rulebook_version"?: string | null,"verified_at"?: string | null
                  }
                  Update: {
                    "allowed_alteration_types"?: NonNullable<Json>,"disclosure_labels"?: NonNullable<Json>,"display_name"?: string,"enumerated_field_lookups"?: NonNullable<Json>,"forbidden_in_public_remarks"?: NonNullable<Json>,"id"?: string,"photo_max_count"?: number | null,"photo_min_count"?: number | null,"photo_min_height"?: number | null,"photo_min_width"?: number | null,"private_remarks_max_chars"?: number | null,"public_remarks_max_chars"?: number | null,"required_fields"?: NonNullable<Json>,"rulebook_version"?: string | null,"verified_at"?: string | null
                  }
                  Relationships: [
                    
                  ]
                },"photo_analyses": {
                  Row: {
                    "alt_text": string | null,"content_flags": NonNullable<Json>,"cover_recommended": boolean,"created_at": string,"failed": boolean,"failure_reason": string | null,"feature_tags": NonNullable<Json>,"id": string,"model_id": string | null,"order_recommendation": number | null,"photo_id": string,"prompt_hash": string | null,"prompt_version": string | null,"quality_flags": NonNullable<Json>,"raw_output": Json | null,"recommendation_reason": string | null,"room_type": Database["public"]['Enums']["room_type"] | null,"room_type_confidence": number | null
                  }
                  ComputedFields: never
                  Insert: {
                    "alt_text"?: string | null,"content_flags"?: NonNullable<Json>,"cover_recommended"?: boolean,"created_at"?: string,"failed"?: boolean,"failure_reason"?: string | null,"feature_tags"?: NonNullable<Json>,"id"?: string,"model_id"?: string | null,"order_recommendation"?: number | null,"photo_id": string,"prompt_hash"?: string | null,"prompt_version"?: string | null,"quality_flags"?: NonNullable<Json>,"raw_output"?: Json | null,"recommendation_reason"?: string | null,"room_type"?: Database["public"]['Enums']["room_type"] | null,"room_type_confidence"?: number | null
                  }
                  Update: {
                    "alt_text"?: string | null,"content_flags"?: NonNullable<Json>,"cover_recommended"?: boolean,"created_at"?: string,"failed"?: boolean,"failure_reason"?: string | null,"feature_tags"?: NonNullable<Json>,"id"?: string,"model_id"?: string | null,"order_recommendation"?: number | null,"photo_id"?: string,"prompt_hash"?: string | null,"prompt_version"?: string | null,"quality_flags"?: NonNullable<Json>,"raw_output"?: Json | null,"recommendation_reason"?: string | null,"room_type"?: Database["public"]['Enums']["room_type"] | null,"room_type_confidence"?: number | null
                  }
                  Relationships: [
                    {
      foreignKeyName: "photo_analyses_photo_id_fkey"
      columns: ["photo_id"]
isOneToOne: true
      referencedRelation: "photos"
      referencedColumns: ["id"]
    }
                  ]
                },"photo_embeddings": {
                  Row: {
                    "embedding": string,"model_id": string,"photo_id": string
                  }
                  ComputedFields: never
                  Insert: {
                    "embedding": string,"model_id": string,"photo_id": string
                  }
                  Update: {
                    "embedding"?: string,"model_id"?: string,"photo_id"?: string
                  }
                  Relationships: [
                    {
      foreignKeyName: "photo_embeddings_photo_id_fkey"
      columns: ["photo_id"]
isOneToOne: true
      referencedRelation: "photos"
      referencedColumns: ["id"]
    }
                  ]
                },"photos": {
                  Row: {
                    "alteration_type": Database["public"]['Enums']["alteration_type"] | null,"altered": boolean,"bytes": number | null,"caption": string | null,"checksum_sha256": string,"disclosure_label": string | null,"height": number | null,"id": string,"is_cover": boolean,"listing_id": string,"mime_type": string,"normalized_storage_key": string | null,"order_index": number,"original_storage_key": string,"paired_original_id": string | null,"uploaded_at": string,"width": number | null
                  }
                  ComputedFields: never
                  Insert: {
                    "alteration_type"?: Database["public"]['Enums']["alteration_type"] | null,"altered"?: boolean,"bytes"?: number | null,"caption"?: string | null,"checksum_sha256": string,"disclosure_label"?: string | null,"height"?: number | null,"id"?: string,"is_cover"?: boolean,"listing_id": string,"mime_type": string,"normalized_storage_key"?: string | null,"order_index": number,"original_storage_key": string,"paired_original_id"?: string | null,"uploaded_at"?: string,"width"?: number | null
                  }
                  Update: {
                    "alteration_type"?: Database["public"]['Enums']["alteration_type"] | null,"altered"?: boolean,"bytes"?: number | null,"caption"?: string | null,"checksum_sha256"?: string,"disclosure_label"?: string | null,"height"?: number | null,"id"?: string,"is_cover"?: boolean,"listing_id"?: string,"mime_type"?: string,"normalized_storage_key"?: string | null,"order_index"?: number,"original_storage_key"?: string,"paired_original_id"?: string | null,"uploaded_at"?: string,"width"?: number | null
                  }
                  Relationships: [
                    {
      foreignKeyName: "photos_listing_id_fkey"
      columns: ["listing_id"]
isOneToOne: false
      referencedRelation: "listings"
      referencedColumns: ["id"]
    },{
      foreignKeyName: "photos_paired_original_id_fkey"
      columns: ["paired_original_id"]
isOneToOne: false
      referencedRelation: "photos"
      referencedColumns: ["id"]
    }
                  ]
                },"profiles": {
                  Row: {
                    "accepted_terms_at": string,"api_usage_count": number,"company_name": string | null,"full_name": string | null,"id": string,"license_number": string | null,"license_state": string | null,"platform_admin": boolean,"preferred_language": string,"profile_completed_at": string | null,"subscription_tier": string,"updated_at": string
                  }
                  ComputedFields: never
                  Insert: {
                    "accepted_terms_at"?: string,"api_usage_count"?: number,"company_name"?: string | null,"full_name"?: string | null,"id": string,"license_number"?: string | null,"license_state"?: string | null,"platform_admin"?: boolean,"preferred_language"?: string,"profile_completed_at"?: string | null,"subscription_tier"?: string,"updated_at"?: string
                  }
                  Update: {
                    "accepted_terms_at"?: string,"api_usage_count"?: number,"company_name"?: string | null,"full_name"?: string | null,"id"?: string,"license_number"?: string | null,"license_state"?: string | null,"platform_admin"?: boolean,"preferred_language"?: string,"profile_completed_at"?: string | null,"subscription_tier"?: string,"updated_at"?: string
                  }
                  Relationships: [
                    
                  ]
                },"prompt_versions": {
                  Row: {
                    "active": boolean,"created_at": string,"id": string,"output_schema": NonNullable<Json>,"prompt_hash": string,"task_key": string,"version": number
                  }
                  ComputedFields: never
                  Insert: {
                    "active"?: boolean,"created_at"?: string,"id"?: string,"output_schema": NonNullable<Json>,"prompt_hash": string,"task_key": string,"version": number
                  }
                  Update: {
                    "active"?: boolean,"created_at"?: string,"id"?: string,"output_schema"?: NonNullable<Json>,"prompt_hash"?: string,"task_key"?: string,"version"?: number
                  }
                  Relationships: [
                    
                  ]
                }
          }
          Views: {
            [_ in never]: never
          }
          Functions: {
            "can_read_agent_as_broker_admin":
{ Args: { "p_agent_id": string }; Returns: boolean
                           },
"is_approved_broker_admin":
{ Args: { "p_brokerage_id": string }; Returns: boolean
                           },
"is_approved_member":
{ Args: { "p_brokerage_id": string }; Returns: boolean
                           },
"is_platform_admin":
{ Args: Record<PropertyKey, never>; Returns: boolean
                           }
          }
          Enums: {
            "alteration_type": "virtual_staging"|"other","audit_action": "ai_call"|"field_edit"|"field_confirm"|"photo_override"|"photo_reorder"|"photo_delete"|"finding_raised"|"finding_acknowledged"|"content_generated"|"content_edited"|"approval_granted"|"approval_revoked"|"approval_rejected_hash_mismatch"|"membership_requested"|"membership_decided"|"registry_changed"|"listing_soft_deleted"|"originals_purged","check_type": "fair_housing"|"risky_claim"|"mls_rule"|"photo_integrity"|"advertising_disclosure","content_kind": "public_remarks"|"private_remarks"|"headline","content_status": "draft"|"approved"|"stale","finding_severity": "blocking"|"warning","finding_source": "lexicon"|"judge"|"deterministic","finding_status": "open"|"resolved"|"acknowledged"|"stale","listing_status": "draft"|"analyzing"|"ready_for_review"|"approved"|"archived","membership_role": "agent"|"broker_admin","membership_status": "pending"|"approved"|"rejected"|"revoked","model_eval_status": "unverified"|"passed"|"failed","provenance": "agent_entered"|"public_record"|"ai_detected_from_photo"|"ai_suggested","room_type": "kitchen"|"living_room"|"primary_bedroom"|"bedroom"|"bathroom"|"exterior_front"|"backyard"|"pool"|"garage"|"view"|"floor_plan"|"other","tone": "professional"|"warm"|"luxury"|"concise"
          }
          CompositeTypes: {
            [_ in never]: never
          }
        }
}

type DatabaseWithoutInternals = Omit<Database, '__InternalSupabase'>

type DefaultSchema = DatabaseWithoutInternals[Extract<keyof Database, "public">]

export type Tables<
  DefaultSchemaTableNameOrOptions extends
    | keyof (DefaultSchema["Tables"] & DefaultSchema["Views"])
    | { schema: keyof DatabaseWithoutInternals },
  TableName extends DefaultSchemaTableNameOrOptions extends {
    schema: keyof DatabaseWithoutInternals
  }
    ? keyof (DatabaseWithoutInternals[DefaultSchemaTableNameOrOptions["schema"]]["Tables"] &
        DatabaseWithoutInternals[DefaultSchemaTableNameOrOptions["schema"]]["Views"])
    : never = never
> = DefaultSchemaTableNameOrOptions extends { schema: keyof DatabaseWithoutInternals }
  ? (DatabaseWithoutInternals[DefaultSchemaTableNameOrOptions["schema"]]["Tables"] &
      DatabaseWithoutInternals[DefaultSchemaTableNameOrOptions["schema"]]["Views"])[TableName] extends {
      Row: infer R
    }
    ? R
    : never
  : DefaultSchemaTableNameOrOptions extends keyof (DefaultSchema["Tables"] & DefaultSchema["Views"])
  ? (DefaultSchema["Tables"] & DefaultSchema["Views"])[DefaultSchemaTableNameOrOptions] extends {
      Row: infer R
    }
    ? R
    : never
  : never

export type TablesInsert<
  DefaultSchemaTableNameOrOptions extends
    | keyof DefaultSchema["Tables"]
    | { schema: keyof DatabaseWithoutInternals },
  TableName extends DefaultSchemaTableNameOrOptions extends {
    schema: keyof DatabaseWithoutInternals
  }
    ? keyof DatabaseWithoutInternals[DefaultSchemaTableNameOrOptions["schema"]]["Tables"]
    : never = never
> = DefaultSchemaTableNameOrOptions extends { schema: keyof DatabaseWithoutInternals }
  ? DatabaseWithoutInternals[DefaultSchemaTableNameOrOptions["schema"]]["Tables"][TableName] extends {
      Insert: infer I
    }
    ? I
    : never
  : DefaultSchemaTableNameOrOptions extends keyof DefaultSchema["Tables"]
  ? DefaultSchema["Tables"][DefaultSchemaTableNameOrOptions] extends {
      Insert: infer I
    }
    ? I
    : never
  : never

export type TablesUpdate<
  DefaultSchemaTableNameOrOptions extends
    | keyof DefaultSchema["Tables"]
    | { schema: keyof DatabaseWithoutInternals },
  TableName extends DefaultSchemaTableNameOrOptions extends {
    schema: keyof DatabaseWithoutInternals
  }
    ? keyof DatabaseWithoutInternals[DefaultSchemaTableNameOrOptions["schema"]]["Tables"]
    : never = never
> = DefaultSchemaTableNameOrOptions extends { schema: keyof DatabaseWithoutInternals }
  ? DatabaseWithoutInternals[DefaultSchemaTableNameOrOptions["schema"]]["Tables"][TableName] extends {
      Update: infer U
    }
    ? U
    : never
  : DefaultSchemaTableNameOrOptions extends keyof DefaultSchema["Tables"]
  ? DefaultSchema["Tables"][DefaultSchemaTableNameOrOptions] extends {
      Update: infer U
    }
    ? U
    : never
  : never

export type Enums<
  DefaultSchemaEnumNameOrOptions extends
    | keyof DefaultSchema["Enums"]
    | { schema: keyof DatabaseWithoutInternals },
  EnumName extends DefaultSchemaEnumNameOrOptions extends {
    schema: keyof DatabaseWithoutInternals
  }
    ? keyof DatabaseWithoutInternals[DefaultSchemaEnumNameOrOptions["schema"]]["Enums"]
    : never = never
> = DefaultSchemaEnumNameOrOptions extends { schema: keyof DatabaseWithoutInternals }
  ? DatabaseWithoutInternals[DefaultSchemaEnumNameOrOptions["schema"]]["Enums"][EnumName]
  : DefaultSchemaEnumNameOrOptions extends keyof DefaultSchema["Enums"]
  ? DefaultSchema["Enums"][DefaultSchemaEnumNameOrOptions]
  : never

export type CompositeTypes<
  PublicCompositeTypeNameOrOptions extends
    | keyof DefaultSchema["CompositeTypes"]
    | { schema: keyof DatabaseWithoutInternals },
  CompositeTypeName extends PublicCompositeTypeNameOrOptions extends {
    schema: keyof DatabaseWithoutInternals
  }
    ? keyof DatabaseWithoutInternals[PublicCompositeTypeNameOrOptions["schema"]]["CompositeTypes"]
    : never = never
> = PublicCompositeTypeNameOrOptions extends { schema: keyof DatabaseWithoutInternals }
  ? DatabaseWithoutInternals[PublicCompositeTypeNameOrOptions["schema"]]["CompositeTypes"][CompositeTypeName]
  : PublicCompositeTypeNameOrOptions extends keyof DefaultSchema["CompositeTypes"]
  ? DefaultSchema["CompositeTypes"][PublicCompositeTypeNameOrOptions]
  : never

export const Constants = {
  "public": {
          Enums: {
            "alteration_type": ["virtual_staging", "other"],"audit_action": ["ai_call", "field_edit", "field_confirm", "photo_override", "photo_reorder", "photo_delete", "finding_raised", "finding_acknowledged", "content_generated", "content_edited", "approval_granted", "approval_revoked", "approval_rejected_hash_mismatch", "membership_requested", "membership_decided", "registry_changed", "listing_soft_deleted", "originals_purged"],"check_type": ["fair_housing", "risky_claim", "mls_rule", "photo_integrity", "advertising_disclosure"],"content_kind": ["public_remarks", "private_remarks", "headline"],"content_status": ["draft", "approved", "stale"],"finding_severity": ["blocking", "warning"],"finding_source": ["lexicon", "judge", "deterministic"],"finding_status": ["open", "resolved", "acknowledged", "stale"],"listing_status": ["draft", "analyzing", "ready_for_review", "approved", "archived"],"membership_role": ["agent", "broker_admin"],"membership_status": ["pending", "approved", "rejected", "revoked"],"model_eval_status": ["unverified", "passed", "failed"],"provenance": ["agent_entered", "public_record", "ai_detected_from_photo", "ai_suggested"],"room_type": ["kitchen", "living_room", "primary_bedroom", "bedroom", "bathroom", "exterior_front", "backyard", "pool", "garage", "view", "floor_plan", "other"],"tone": ["professional", "warm", "luxury", "concise"]
          }
        }
} as const
