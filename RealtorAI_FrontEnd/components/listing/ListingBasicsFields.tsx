"use client";

import type { Dictionary } from "@/lib/i18n/getDictionary";
import { PROPERTY_TYPES } from "@/lib/validation/listing";
import { FormField } from "@/components/ui/FormField";
import { Select } from "@/components/ui/Select";
import { TextField } from "@/components/ui/TextField";
import { Textarea } from "@/components/ui/Textarea";

/** The basics as the form holds them: strings, exactly as typed, until validated. */
export type BasicsFormValues = {
  addressLine1: string;
  city: string;
  state: string;
  postalCode: string;
  propertyType: string;
  listPrice: string;
  bedrooms: string;
  bathrooms: string;
  agentNotes: string;
};

export type BasicsFormField = keyof BasicsFormValues;

export const EMPTY_BASICS: BasicsFormValues = {
  addressLine1: "",
  city: "",
  state: "",
  postalCode: "",
  propertyType: "",
  listPrice: "",
  bedrooms: "",
  bathrooms: "",
  agentNotes: "",
};

/**
 * The six basic details of Story 1, shared by the create form (uncontrolled, submitted with
 * the form) and the draft editor (controlled, autosaved). One component, so the two cannot
 * drift apart in labels, input modes or names.
 */
export function ListingBasicsFields({
  dict,
  propertyTypes,
  errors,
  values,
  onChange,
  onBlur,
}: {
  dict: Dictionary["listings"]["basics"];
  propertyTypes: Dictionary["listings"]["propertyTypes"];
  errors: Partial<Record<BasicsFormField, string>>;
  /** Present for the controlled editor; absent for the create form. */
  values?: BasicsFormValues;
  onChange?: (field: BasicsFormField, value: string) => void;
  onBlur?: () => void;
}) {
  const bind = (field: BasicsFormField) =>
    values && onChange
      ? {
          value: values[field],
          onChange: (event: { currentTarget: { value: string } }) => onChange(field, event.currentTarget.value),
          onBlur,
        }
      : {};

  return (
    <div className="space-y-5">
      <TextField
        id="listing-address"
        name="addressLine1"
        label={dict.address}
        error={errors.addressLine1}
        required
        autoComplete="address-line1"
        {...bind("addressLine1")}
      />

      <div className="grid gap-5 sm:grid-cols-3">
        <TextField
          id="listing-city"
          name="city"
          label={dict.city}
          error={errors.city}
          required
          autoComplete="address-level2"
          {...bind("city")}
        />
        <TextField
          id="listing-state"
          name="state"
          label={dict.state}
          hint={dict.stateHint}
          error={errors.state}
          maxLength={2}
          required
          autoComplete="address-level1"
          {...bind("state")}
        />
        <TextField
          id="listing-postal-code"
          name="postalCode"
          label={dict.postalCode}
          error={errors.postalCode}
          inputMode="numeric"
          maxLength={10}
          required
          autoComplete="postal-code"
          {...bind("postalCode")}
        />
      </div>

      <FormField id="listing-property-type" label={dict.propertyType} error={errors.propertyType}>
        {(describedBy) => (
          <Select
            id="listing-property-type"
            name="propertyType"
            required
            invalid={!!errors.propertyType}
            aria-describedby={describedBy}
            {...(values && onChange
              ? {
                  value: values.propertyType,
                  onChange: (event) => onChange("propertyType", event.currentTarget.value),
                  onBlur,
                }
              : { defaultValue: "" })}
          >
            <option value="" disabled>
              {dict.chooseType}
            </option>
            {PROPERTY_TYPES.map((type) => (
              <option key={type} value={type}>
                {propertyTypes[type]}
              </option>
            ))}
          </Select>
        )}
      </FormField>

      <div className="grid gap-5 sm:grid-cols-3">
        <TextField
          id="listing-price"
          name="listPrice"
          label={dict.listPrice}
          hint={dict.listPriceHint}
          error={errors.listPrice}
          inputMode="decimal"
          {...bind("listPrice")}
        />
        <TextField
          id="listing-bedrooms"
          name="bedrooms"
          label={dict.bedrooms}
          error={errors.bedrooms}
          inputMode="numeric"
          {...bind("bedrooms")}
        />
        <TextField
          id="listing-bathrooms"
          name="bathrooms"
          label={dict.bathrooms}
          hint={dict.bathroomsHint}
          error={errors.bathrooms}
          inputMode="decimal"
          {...bind("bathrooms")}
        />
      </div>

      <FormField id="listing-notes" label={dict.agentNotes} hint={dict.agentNotesHint} error={errors.agentNotes}>
        {(describedBy) => (
          <Textarea
            id="listing-notes"
            name="agentNotes"
            rows={3}
            maxLength={5000}
            invalid={!!errors.agentNotes}
            aria-describedby={describedBy}
            {...(values && onChange
              ? {
                  value: values.agentNotes,
                  onChange: (event) => onChange("agentNotes", event.currentTarget.value),
                  onBlur,
                }
              : {})}
          />
        )}
      </FormField>
    </div>
  );
}
