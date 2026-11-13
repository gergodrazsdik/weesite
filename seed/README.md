# seed

The sample site that local development, the tests and the test environment run on: about 100 posts with photos, a few in Hungarian, some drafts and one scheduled. The tests read it directly (`Content:Path`). With `Seed:Path` pointing here, the web app uploads it into Blob Storage at startup (`just aspire`); test gets it from the **seed** workflow, run by hand, which runs the same seeder (the app's `seed` command; ADR 0002 amendment). Each top-level directory becomes a container, each file a blob named by its path inside it. Only missing files and files changed in `seed/` are written; a file changed in the editor is kept. The image doesn't carry it, and prod never loads it.

| Directory | Container | What |
| --- | --- | --- |
| `content/posts/` | `content` | Blog posts: Markdown with YAML front matter |
| `content/pages/` | `content` | Content pages (`kind: page`) |
| `settings/` | `settings` | Site settings (`site.json`) with the placeholder identity; `language` (such as `en` or `hu`) sets the culture of every page (month names, the lists' short dates, `og:locale`), and `dateFormat` how dates are written for readers (a .NET date format, default `yyyy-MM-dd`). `links` are the site's links in the footer (else the first author's). Admins edit these at `/admin/settings`. Editors' profiles go in `authors.json` beside it, written from `/admin/profile` |
| `media/` | `media` | Images and attachments, named `{hash}-{name}` (the first 12 hex digits of the file's SHA-256), served at `/media/{name}`. Seeding strips the images' metadata and adds WebP variants; `c0f10a37852f-gradient.jpg` carries made-up camera and GPS tags to show that. |

The identity is a placeholder ("Example Author", `example.com`): never put real names or addresses here. Front matter keys: `kind` (`post` or `page`), `title`, `slug`, `date` (posts), `updated`, `author` (an ID in `site.json`), `lang` (when an item isn't in the site's language), `tags`, `summary`, `menu` (pages), `redirect_from` (earlier slugs), `image`, `image_alt`, `status` (`published`, the default; `draft`; or `scheduled` with `publish_at`, a time with an offset such as `2030-01-01T08:00:00Z`; ADR 0005). Only published items, and scheduled ones once due, reach the public site: `notes-for-a-future-post` is a draft and `a-post-from-the-future` is scheduled. Raw HTML in the Markdown is shown as text (ADR 0002).

## Photos

The photos in `media/` come from Wikimedia Commons. Each one is public domain or CC0 there, so it needs no attribution; it's credited anyway. They were scaled to 1280 pixels wide (the later ones to 1200) and re-encoded without metadata.

| File | Source | Author | Licence |
| --- | --- | --- | --- |
| `854ec192d6e7-audio-plug.jpg` | [Audio-TRS-Mini-Plug.jpg](https://commons.wikimedia.org/wiki/File:Audio-TRS-Mini-Plug.jpg) | Evan-Amos | Public domain |
| `363ad5a444bf-city-night.jpg` | [Price Building illuminated at night in Quebec City.jpg](https://commons.wikimedia.org/wiki/File:Price_Building_illuminated_at_night_in_Quebec_City.jpg) | Wilfredor | CC0 |
| `7e354e2b7180-crete-coast.jpg` | [Komos site baie Crète.jpg](https://commons.wikimedia.org/wiki/File:Komos_site_baie_Cr%C3%A8te.jpg) | Jebulon | CC0 |
| `4c01a5e824a1-espresso.jpg` | [Espresso Coffee 01.jpg](https://commons.wikimedia.org/wiki/File:Espresso_Coffee_01.jpg) | Jubair1985 | CC0 |
| `116c3f9b1f40-keyboard.jpg` | [Keyboard Close Up.jpg](https://commons.wikimedia.org/wiki/File:Keyboard_Close_Up.jpg) | Alexander-design | CC0 |
| `c413646f0110-lake-bridge.jpg` | [General Rafael Urdaneta Bridge view from the lake to Cabimas side.jpg](https://commons.wikimedia.org/wiki/File:General_Rafael_Urdaneta_Bridge_view_from_the_lake_to_Cabimas_side.jpg) | Wilfredor | CC0 |
| `82adc6cea959-lighthouse.jpg` | [Lime Point Lighthouse, SW view.jpg](https://commons.wikimedia.org/wiki/File:Lime_Point_Lighthouse,_SW_view.jpg) | Radomianin | Public domain |
| `e2f2cf3360b3-marigolds.jpg` | [French marigold garden 2009 G1.jpg](https://commons.wikimedia.org/wiki/File:French_marigold_garden_2009_G1.jpg) | George Chernilevsky | Public domain |
| `690a2c4f2bca-monastery-library.jpg` | [2024 Kłodzko, zespół klasztorny franciszkanów, biblioteka (2).jpg](https://commons.wikimedia.org/wiki/File:2024_K%C5%82odzko,_zesp%C3%B3%C5%82_klasztorny_franciszkan%C3%B3w,_biblioteka_(2).jpg) | Jacek Halicki | CC0 |
| `dc52c9498729-railway-bridge.jpg` | [JA1271 crossing the Otaki River.jpg](https://commons.wikimedia.org/wiki/File:JA1271_crossing_the_Otaki_River.jpg) | Karora | Public domain |
| `ecbf632d0a3e-snowy-bike.jpg` | [Bike on a snowy street in Quebec City at night.jpg](https://commons.wikimedia.org/wiki/File:Bike_on_a_snowy_street_in_Quebec_City_at_night.jpg) | Wilfredor | CC0 |
| `5f3313d573ee-snowy-road.jpg` | [Forest road Slavne 2017 G8.jpg](https://commons.wikimedia.org/wiki/File:Forest_road_Slavne_2017_G8.jpg) | George Chernilevsky | Public domain |
| `c0d16c7a20e9-spruce-forest.jpg` | [Spruce forest at Holma.jpg](https://commons.wikimedia.org/wiki/File:Spruce_forest_at_Holma.jpg) | W.carter | CC0 |
| `ee3a9b622ebb-sunset.jpg` | [Juan Griego Sunset.jpg](https://commons.wikimedia.org/wiki/File:Juan_Griego_Sunset.jpg) | Wilfredor | CC0 |
| `3ca24cb3a95a-agave-flower.jpg` | [Agave Flower.jpg](https://commons.wikimedia.org/wiki/File:Agave_Flower.jpg) | Wilfredor | CC0 |
| `1005e9cec739-almond-blossom.jpg` | [Amandier Pinar.jpg](https://commons.wikimedia.org/wiki/File:Amandier_Pinar.jpg) | Jebulon | Public domain |
| `e6071693c6a6-almond-farm.jpg` | [Almonds plantation and farm, Alhama de Granada, Andalusia, Spain.jpg](https://commons.wikimedia.org/wiki/File:Almonds_plantation_and_farm,_Alhama_de_Granada,_Andalusia,_Spain.jpg) | Jebulon | CC0 |
| `d837fae023cb-almond-ruin.jpg` | [Almond trees, ruined house, Andalusia, Spain.jpg](https://commons.wikimedia.org/wiki/File:Almond_trees,_ruined_house,_Andalusia,_Spain.jpg) | Jebulon | CC0 |
| `6ee2d2c19978-almonds.jpg` | [Almonds in a tree, Andalusia, Spain.jpg](https://commons.wikimedia.org/wiki/File:Almonds_in_a_tree,_Andalusia,_Spain.jpg) | Jebulon | CC0 |
| `fe7ef2decb3f-amphitheatre.jpg` | [Amphitheatre 1 Pompeii.jpg](https://commons.wikimedia.org/wiki/File:Amphitheatre_1_Pompeii.jpg) | Jebulon | CC0 |
| `155ed262a41d-amphoras.jpg` | [Amphoras storage Herculaneum.jpg](https://commons.wikimedia.org/wiki/File:Amphoras_storage_Herculaneum.jpg) | Jebulon | CC0 |
| `19e6c611344d-anchors.jpg` | [Anchors in Spring Chacachacare.jpg](https://commons.wikimedia.org/wiki/File:Anchors_in_Spring_Chacachacare.jpg) | Wilfredor | CC0 |
| `c7990f7a267b-autumn-forest.jpg` | [Autumn forest near Avenue du Bourg-Royal, Quebec City.jpg](https://commons.wikimedia.org/wiki/File:Autumn_forest_near_Avenue_du_Bourg-Royal,_Quebec_City.jpg) | Wilfredor | CC0 |
| `5d8ec1f640ae-autumn-quebec.jpg` | [Autumn in Quebec City (in autumn 02).jpg](https://commons.wikimedia.org/wiki/File:Autumn_in_Quebec_City_(in_autumn_02).jpg) | Wilfredor | CC0 |
| `28bae3300845-avenue-at-night.jpg` | [Avenue Cartier illuminated at night in Quebec City.jpg](https://commons.wikimedia.org/wiki/File:Avenue_Cartier_illuminated_at_night_in_Quebec_City.jpg) | Wilfredor | CC0 |
| `8e39704dde24-barber-shop.jpg` | [Barber shop, Quebec city, Canada.jpg](https://commons.wikimedia.org/wiki/File:Barber_shop,_Quebec_city,_Canada.jpg) | Wilfredor | CC0 |
| `45920d364aa7-beach-path.jpg` | [Accès plage, Sainte-Marie, Ré island, august 2015.jpg](https://commons.wikimedia.org/wiki/File:Acc%C3%A8s_plage,_Sainte-Marie,_R%C3%A9_island,_august_2015.jpg) | Jebulon | CC0 |
| `9e1ab7afa008-bicycle-parking.jpg` | [Bicycle parking in Deutsches Museum - Building.jpg](https://commons.wikimedia.org/wiki/File:Bicycle_parking_in_Deutsches_Museum_-_Building.jpg) | Wilfredor | CC0 |
| `faa1ebeebbfa-boats-aground.jpg` | [A day of fishing aground.jpg](https://commons.wikimedia.org/wiki/File:A_day_of_fishing_aground.jpg) | Wilfredor | CC0 |
| `694bc0c2df85-brick-houses.jpg` | [178 Rue Aberdeen.jpg](https://commons.wikimedia.org/wiki/File:178_Rue_Aberdeen.jpg) | Wilfredor | CC0 |
| `3903ebf9f0f9-butterfly.jpg` | [Agraulis vanillae at Isla Margarita.jpg](https://commons.wikimedia.org/wiki/File:Agraulis_vanillae_at_Isla_Margarita.jpg) | Wilfredor | CC0 |
| `336828587b4e-charred-shutters.jpg` | [Ancient roman burnt window shutters Herculaneum.jpg](https://commons.wikimedia.org/wiki/File:Ancient_roman_burnt_window_shutters_Herculaneum.jpg) | Jebulon | CC0 |
| `275dbb9627a7-city-from-hill.jpg` | [Agios Nikolaos from Lycabetus Athens.jpg](https://commons.wikimedia.org/wiki/File:Agios_Nikolaos_from_Lycabetus_Athens.jpg) | Jebulon | CC0 |
| `bfaa4bf486ae-classic-car.jpg` | [1952 Hudson Hornet in Quebec City St. Patrick's Day Parade 2025 45.jpg](https://commons.wikimedia.org/wiki/File:1952_Hudson_Hornet_in_Quebec_City_St._Patrick%27s_Day_Parade_2025_45.jpg) | Wilfredor | CC0 |
| `fc424c101adc-clay-jars.jpg` | [Ancient roman jars at Pompeii.jpg](https://commons.wikimedia.org/wiki/File:Ancient_roman_jars_at_Pompeii.jpg) | Jebulon | CC0 |
| `e162293717df-cliff-shelter.jpg` | [Abri troglodyte Roque Saint-Christophe.jpg](https://commons.wikimedia.org/wiki/File:Abri_troglodyte_Roque_Saint-Christophe.jpg) | Jebulon | CC0 |
| `6cffec95c42d-crowned-crane.jpg` | [Balearica regulorum in Zoológico de São Paulo.jpg](https://commons.wikimedia.org/wiki/File:Balearica_regulorum_in_Zool%C3%B3gico_de_S%C3%A3o_Paulo.jpg) | Wilfredor | CC0 |
| `426dc6520e96-cyclist-abbey.jpg` | [Abbaye Notre-Dame de Ré Île de Ré cycliste Charente-Maritime.jpg](https://commons.wikimedia.org/wiki/File:Abbaye_Notre-Dame_de_R%C3%A9_%C3%8Ele_de_R%C3%A9_cycliste_Charente-Maritime.jpg) | Jebulon | CC0 |
| `9914c25a852a-cypress-alley.jpg` | [Alley Generalife 1 Grenade.jpg](https://commons.wikimedia.org/wiki/File:Alley_Generalife_1_Grenade.jpg) | Jebulon | CC0 |
| `54e8d306b46c-ducks.jpg` | [Anas platyrhynchos in Montreal, Canada.jpg](https://commons.wikimedia.org/wiki/File:Anas_platyrhynchos_in_Montreal,_Canada.jpg) | Wilfredor | CC0 |
| `0b5389dec5f5-ferry.jpg` | [ANEK LINES EL.Venizelos front view, Piraeus, Greece.jpg](https://commons.wikimedia.org/wiki/File:ANEK_LINES_EL.Venizelos_front_view,_Piraeus,_Greece.jpg) | Jebulon | CC0 |
| `aadfe6db380b-fishing-boat.jpg` | [Beached wooden fishing boat leaning beside the lagoon at Chacachacare, Margarita Island, Venezuela.jpg](https://commons.wikimedia.org/wiki/File:Beached_wooden_fishing_boat_leaning_beside_the_lagoon_at_Chacachacare,_Margarita_Island,_Venezuela.jpg) | Wilfredor | CC0 |
| `46ff964dab23-forest-sunset.jpg` | [Atardecer en Selva del Estado Anzoategui en Chaguaramal.jpg](https://commons.wikimedia.org/wiki/File:Atardecer_en_Selva_del_Estado_Anzoategui_en_Chaguaramal.jpg) | Wilfredor | CC0 |
| `dcfec1669878-fortress-walls.jpg` | [Alcazaba 1, Almeria, Spain.jpg](https://commons.wikimedia.org/wiki/File:Alcazaba_1,_Almeria,_Spain.jpg) | Jebulon | CC0 |
| `8acdc7a1f38d-garden-alley.jpg` | [Alley alcazar Seville Andalusia Spain.jpg](https://commons.wikimedia.org/wiki/File:Alley_alcazar_Seville_Andalusia_Spain.jpg) | Jebulon | CC0 |
| `ad095e9b247e-harbour-boats.jpg` | [Aegina harbour 1.jpg](https://commons.wikimedia.org/wiki/File:Aegina_harbour_1.jpg) | Jebulon | CC0 |
| `e7e40f8fdfb6-harbour-sunset.jpg` | [Aegina sunset.jpg](https://commons.wikimedia.org/wiki/File:Aegina_sunset.jpg) | Jebulon | CC0 |
| `d5015db41c24-helicopter-bay.jpg` | [Bell 206L-4 LongRanger IV over Botafogo Bay, Rio de Janeiro.jpg](https://commons.wikimedia.org/wiki/File:Bell_206L-4_LongRanger_IV_over_Botafogo_Bay,_Rio_de_Janeiro.jpg) | Wilfredor | CC0 |
| `c99ed24e6aaa-hill-quarter.jpg` | [Albaizin from Generalife, Granada, Spain.jpg](https://commons.wikimedia.org/wiki/File:Albaizin_from_Generalife,_Granada,_Spain.jpg) | Jebulon | CC0 |
| `4e44ed96954a-hilltop-fortress.jpg` | [Alcazaba Alhambra Granada Spain.jpg](https://commons.wikimedia.org/wiki/File:Alcazaba_Alhambra_Granada_Spain.jpg) | Jebulon | CC0 |
| `9c1f4739df9b-hollyhocks.jpg` | [Alcea rosea, Marsilly, Charente-Maritime, august 2015.jpg](https://commons.wikimedia.org/wiki/File:Alcea_rosea,_Marsilly,_Charente-Maritime,_august_2015.jpg) | Jebulon | CC0 |
| `82daa660a206-island-barn.jpg` | [Barn, Île d'Orléans, Quebec.jpg](https://commons.wikimedia.org/wiki/File:Barn,_%C3%8Ele_d%27Orl%C3%A9ans,_Quebec.jpg) | Wilfredor | CC0 |
| `7b6dfd0d325a-island-coast.jpg` | [Aegina bay coastline.jpg](https://commons.wikimedia.org/wiki/File:Aegina_bay_coastline.jpg) | Jebulon | CC0 |
| `27fe780c1c61-island-sunset.jpg` | [Backlit Margarita Island Sunset in Las Guevaras, Venezuela CaptureNX2.jpg](https://commons.wikimedia.org/wiki/File:Backlit_Margarita_Island_Sunset_in_Las_Guevaras,_Venezuela_CaptureNX2.jpg) | Wilfredor | CC0 |
| `06bd645d47dc-lake-trail.jpg` | [Base de plein air de Sainte-Foy, Lacs Laberge 002.jpg](https://commons.wikimedia.org/wiki/File:Base_de_plein_air_de_Sainte-Foy,_Lacs_Laberge_002.jpg) | Wilfredor | CC0 |
| `3edf2dc3489c-lindos-acropolis.jpg` | [Acropolis Athena Lindos Rhodes 2.jpg](https://commons.wikimedia.org/wiki/File:Acropolis_Athena_Lindos_Rhodes_2.jpg) | Jebulon | CC0 |
| `761ec44946e4-lindos-hill.jpg` | [Acropolis Lindos Rhodes 1.jpg](https://commons.wikimedia.org/wiki/File:Acropolis_Lindos_Rhodes_1.jpg) | Jebulon | CC0 |
| `42d4961b3539-maple-rocks.jpg` | [Acer monspessulanum Torcal.jpg](https://commons.wikimedia.org/wiki/File:Acer_monspessulanum_Torcal.jpg) | Jebulon | CC0 |
| `5167ab9bb5b3-moored-boats.jpg` | [Alignement d'annexes, Conleau, golfe du Morbihan, France.jpg](https://commons.wikimedia.org/wiki/File:Alignement_d%27annexes,_Conleau,_golfe_du_Morbihan,_France.jpg) | Jebulon | CC0 |
| `d5316b7a0b74-mountain-almonds.jpg` | [Amandiers Sierra Nevada.jpg](https://commons.wikimedia.org/wiki/File:Amandiers_Sierra_Nevada.jpg) | Jebulon | CC0 |
| `e373fd4dd04e-mountain-tree.jpg` | [Ailanthus altissima august 2012 Sierra Nevada Andalusia Spain.jpg](https://commons.wikimedia.org/wiki/File:Ailanthus_altissima_august_2012_Sierra_Nevada_Andalusia_Spain.jpg) | Jebulon | CC0 |
| `82b57ce7e560-old-anchor.jpg` | [Ancre Karystos.jpg](https://commons.wikimedia.org/wiki/File:Ancre_Karystos.jpg) | Jebulon | CC0 |
| `cee2d28293a3-old-bank.jpg` | [Bank of Montreal, Quebec city.jpg](https://commons.wikimedia.org/wiki/File:Bank_of_Montreal,_Quebec_city.jpg) | Wilfredor | CC0 |
| `a8363ca3a439-opera-house.jpg` | [Bavarian State Opera, Munich.jpg](https://commons.wikimedia.org/wiki/File:Bavarian_State_Opera,_Munich.jpg) | Wilfredor | CC0 |
| `6f7635c978c7-paella.jpg` | [2012 Paëlla Andalusia Spain.jpg](https://commons.wikimedia.org/wiki/File:2012_Pa%C3%ABlla_Andalusia_Spain.jpg) | Jebulon | CC0 |
| `979db624b962-palace-at-dusk.jpg` | [Alhambra at dusk Granada Spain.jpg](https://commons.wikimedia.org/wiki/File:Alhambra_at_dusk_Granada_Spain.jpg) | Jebulon | CC0 |
| `d7eb865de6ad-palace-from-below.jpg` | [Alhambra from below Paseo de los Tristes Granada Spain.jpg](https://commons.wikimedia.org/wiki/File:Alhambra_from_below_Paseo_de_los_Tristes_Granada_Spain.jpg) | Jebulon | CC0 |
| `f59a58e2ca54-palace-gardens.jpg` | [Alcazar de los reyes cristianos gardens Cordoba.jpg](https://commons.wikimedia.org/wiki/File:Alcazar_de_los_reyes_cristianos_gardens_Cordoba.jpg) | Jebulon | Public domain |
| `8dd5ccd5fe0f-palace-hill.jpg` | [Alhambra hill over Granada Spain.jpg](https://commons.wikimedia.org/wiki/File:Alhambra_hill_over_Granada_Spain.jpg) | Jebulon | CC0 |
| `b84cf6a94f8c-paris-street.jpg` | [128 Rue Saint-Saëns, 75015 Paris, França, Black and White.jpg](https://commons.wikimedia.org/wiki/File:128_Rue_Saint-Sa%C3%ABns,_75015_Paris,_Fran%C3%A7a,_Black_and_White.jpg) | Wilfredor | CC0 |
| `12a1f3757f14-parthenon.jpg` | [Acropolis Parthenon Athens Greece.jpg](https://commons.wikimedia.org/wiki/File:Acropolis_Parthenon_Athens_Greece.jpg) | Jebulon | CC0 |
| `577792298337-pedestrian-crossing.jpg` | [Avenida Faria Lima, cruce de peatones.jpg](https://commons.wikimedia.org/wiki/File:Avenida_Faria_Lima,_cruce_de_peatones.jpg) | Wilfredor | CC0 |
| `8f843f047ec2-port-chapel.jpg` | [Agios Nikolaos Port Aegina.jpg](https://commons.wikimedia.org/wiki/File:Agios_Nikolaos_Port_Aegina.jpg) | Jebulon | CC0 |
| `c6dc619e172d-quiet-bay.jpg` | [Agios Minas bay Chalkida Greece.jpg](https://commons.wikimedia.org/wiki/File:Agios_Minas_bay_Chalkida_Greece.jpg) | Jebulon | CC0 |
| `a965c519c36d-quiet-street.jpg` | [25e rue, Quebec City (008).jpg](https://commons.wikimedia.org/wiki/File:25e_rue,_Quebec_City_(008).jpg) | Wilfredor | CC0 |
| `94fd58d36d99-red-brick-abbey.jpg` | [1872 building Greyfriars Abbey Odense Denmark.jpg](https://commons.wikimedia.org/wiki/File:1872_building_Greyfriars_Abbey_Odense_Denmark.jpg) | Jebulon | CC0 |
| `17adda9e02fa-roman-arena.jpg` | [Amphithéatre Saintes, Charente-Maritime, august 2015.jpg](https://commons.wikimedia.org/wiki/File:Amphith%C3%A9atre_Saintes,_Charente-Maritime,_august_2015.jpg) | Jebulon | CC0 |
| `bc1c0a3ec363-ruined-abbey.jpg` | [Abbaye Notre-Dame de Ré, Ré island, Charente-Maritime, France.jpg](https://commons.wikimedia.org/wiki/File:Abbaye_Notre-Dame_de_R%C3%A9,_R%C3%A9_island,_Charente-Maritime,_France.jpg) | Jebulon | CC0 |
| `c586b7716b87-sailing-harbour.jpg` | [Aegina sailing harbor.jpg](https://commons.wikimedia.org/wiki/File:Aegina_sailing_harbor.jpg) | Jebulon | CC0 |
| `0e990b53f5db-seashells.jpg` | [Arca zebra (Interior and Exterior).jpg](https://commons.wikimedia.org/wiki/File:Arca_zebra_(Interior_and_Exterior).jpg) | Wilfredor | CC0 |
| `0db0e757d3c8-sundial-tower.jpg` | [Amalienburg clock tower sundial Hofburg Vienna.jpg](https://commons.wikimedia.org/wiki/File:Amalienburg_clock_tower_sundial_Hofburg_Vienna.jpg) | Jebulon | CC0 |
| `698d502ac042-tile-roof-house.jpg` | [Abandoned tile-roofed house in dry scrub, Margarita Island, Venezuela.jpg](https://commons.wikimedia.org/wiki/File:Abandoned_tile-roofed_house_in_dry_scrub,_Margarita_Island,_Venezuela.jpg) | Wilfredor | CC0 |
| `b5fcad111453-tree-avenue.jpg` | [Allée des philosophes château Chantilly.jpg](https://commons.wikimedia.org/wiki/File:All%C3%A9e_des_philosophes_ch%C3%A2teau_Chantilly.jpg) | Jebulon | CC0 |
| `940a5a352d75-two-islands.jpg` | [Agia Triada and Aspronisos islands Eretria Euboea Greece.jpg](https://commons.wikimedia.org/wiki/File:Agia_Triada_and_Aspronisos_islands_Eretria_Euboea_Greece.jpg) | Jebulon | CC0 |
| `614bae16aca8-vienna-street.jpg` | [20 Graben Vienna.jpg](https://commons.wikimedia.org/wiki/File:20_Graben_Vienna.jpg) | Jebulon | CC0 |
| `436e81c51b91-white-hillside.jpg` | [Albaicin 2012 San Nicolas Sacromonte.jpg](https://commons.wikimedia.org/wiki/File:Albaicin_2012_San_Nicolas_Sacromonte.jpg) | Jebulon | CC0 |
