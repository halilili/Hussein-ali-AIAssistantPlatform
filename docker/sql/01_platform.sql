-- Platform schema: tenants (the businesses that subscribe to the assistant)
CREATE SCHEMA IF NOT EXISTS platform;

CREATE TABLE platform.tenants (
    id                 text PRIMARY KEY,                       -- slug, e.g. 'clinic-haddad'
    display_name       text NOT NULL,
    vertical           text NOT NULL CHECK (vertical IN ('clinic', 'restaurant', 'pharmacy')),
    wa_phone_number_id text UNIQUE,                            -- WhatsApp Cloud API phone_number_id -> tenant
    timezone           text NOT NULL DEFAULT 'Asia/Beirut',
    languages          text[] NOT NULL DEFAULT ARRAY['ar', 'en'],
    prompt_version     text NOT NULL DEFAULT 'v1',             -- which prompts/{vertical}/system.{v}.md to load
    settings           jsonb NOT NULL DEFAULT '{}'::jsonb,     -- address, phone, emergency number, ...
    is_active          boolean NOT NULL DEFAULT true,
    created_at         timestamptz NOT NULL DEFAULT now()
);

INSERT INTO platform.tenants (id, display_name, vertical, wa_phone_number_id, timezone, languages, prompt_version, settings) VALUES
('clinic-haddad', 'Dr. Karim Haddad Cardiology Clinic', 'clinic', '100000000000001', 'Asia/Beirut',
 ARRAY['ar', 'en', 'fr'], 'v1',
 '{"address": "Hamra, Bliss Street, Cedar Building, 3rd floor, Beirut",
   "clinic_phone": "+961 1 000 001",
   "emergency_number": "140 (Lebanese Red Cross)"}'),
('smile-dental', 'Smile Dental Center', 'clinic', '100000000000002', 'Asia/Beirut',
 ARRAY['ar', 'en'], 'v1',
 '{"address": "Achrafieh, Sassine Square, Olive Tower, 1st floor, Beirut",
   "clinic_phone": "+961 1 000 002",
   "emergency_number": "140 (Lebanese Red Cross)"}');
