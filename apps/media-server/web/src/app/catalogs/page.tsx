import { AdminOnly } from "@/components/admin-only";
import { CatalogsSection } from "@/components/catalogs-section";

export default function CatalogsPage() {
  return (
    <AdminOnly>
      <CatalogsSection />
    </AdminOnly>
  );
}
